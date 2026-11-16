"""Extract bounded text from locally selected attachments without running document content."""

from decimal import Decimal, InvalidOperation
import io
import json
from pathlib import Path
import posixpath
import re
import sys
from xml.etree import ElementTree as XML
from zipfile import ZipFile


INPUT_LIMIT = 4 * 1024 * 1024
XML_LIMIT = 8 * 1024 * 1024
MAX_PAGES = 24
MAX_SHEETS = 24
OMITTED = "\n[More document content omitted]\n"


class AttachmentError(Exception):
    pass


class Excerpt:
    def __init__(self, limit):
        self.capacity = limit - len(OMITTED)
        self.parts = []
        self.truncated = False

    def add(self, line):
        line = "".join(ch if ch >= " " or ch in "\n\t" else " " for ch in line) + "\n"
        data = line.encode("utf-16-le", errors="replace")
        available = self.capacity * 2
        if len(data) > available:
            self.parts.append(data[:available].decode("utf-16-le", errors="ignore"))
            self.capacity = 0
            self.truncated = True
            return False
        self.capacity -= len(data) // 2
        self.parts.append(line)
        return True

    def result(self, note=None):
        text = "".join(self.parts).strip()
        if self.truncated:
            text += OMITTED
        return {"text": text, "truncated": self.truncated, "note": note}


def read_pdf(raw, excerpt):
    sys.path.insert(0, str(Path(__file__).with_name("attachment-pypdf.zip")))
    from pypdf import PdfReader, overwrite_configuration

    limit = 12 * 1024 * 1024
    overwrite_configuration(
        maximum_declared_stream_length=limit, array_based_stream_maximum_output_length=limit,
        zlib_maximum_output_length=limit, lzw_maximum_output_length=limit, run_length_maximum_output_length=limit,
        brotli_maximum_output_length=limit, stream_decoding_work_maximum_length=48 * 1024 * 1024,
        page_tree_maximum_page_entries=2000, jbig2dec_binary=None,
    )
    reader = PdfReader(io.BytesIO(raw))
    if reader.is_encrypted:
        raise AttachmentError("This PDF is password protected. Attach an unlocked copy.")
    count = len(reader.pages)
    if count == 0:
        raise AttachmentError("This PDF has no pages.")
    blank = 0
    readable = 0
    for index in range(min(count, MAX_PAGES)):
        text = reader.pages[index].extract_text().strip()
        if not text:
            blank += 1
        else:
            readable += 1
        if not excerpt.add(f"Page {index + 1} of {count}\n{text or '[No selectable text on this page]'}\n"):
            break
    if not readable:
        raise AttachmentError("This PDF has no selectable text. For a scanned PDF, attach page images or an OCR text copy.")
    excerpt.truncated |= count > MAX_PAGES
    note = "Some PDF pages had no selectable text." if blank else None
    return excerpt.result(note)


def local_name(tag):
    return tag.rsplit("}", 1)[-1]


def rich_text(item):
    # Display text runs only. Phonetic annotations and markup are not part of the cell value.
    return "".join(node.text or "" for run in item
                   for node in ([run] if local_name(run.tag) == "t" else list(run) if local_name(run.tag) == "r" else [])
                   if local_name(node.tag) == "t")


def read_workbook(raw, excerpt):
    with ZipFile(io.BytesIO(raw)) as archive:
        entries = archive.infolist()
        if len(entries) > 3000 or sum(item.file_size for item in entries) > 48 * 1024 * 1024:
            raise AttachmentError("The Excel workbook expands beyond the attachment reader’s size limits.")

        def xml(name, optional=False):
            try:
                entry = archive.getinfo(name)
            except KeyError:
                if optional:
                    return None
                raise AttachmentError("This Excel workbook is missing a required part.") from None
            if entry.file_size > XML_LIMIT:
                raise AttachmentError("A worksheet or workbook table is too large to read as an attachment.")
            with archive.open(entry) as part:
                data = part.read(XML_LIMIT + 1)
            if len(data) > XML_LIMIT or re.search(rb"<!\s*(?:DOCTYPE|ENTITY)", data.replace(b"\0", b""), re.I):
                raise AttachmentError("The workbook contains unsupported XML.")
            return XML.fromstring(data)

        book = xml("xl/workbook.xml")
        relationships = xml("xl/_rels/workbook.xml.rels")
        targets = {item.get("Id"): item.get("Target", "") for item in relationships
                   if item.get("Type", "").endswith("/worksheet") and item.get("TargetMode") != "External"}
        shared = xml("xl/sharedStrings.xml", optional=True)
        strings = [] if shared is None else [rich_text(item) for item in shared if local_name(item.tag) == "si"]
        styles = xml("xl/styles.xml", optional=True)
        percentages = set()
        if styles is not None:
            formats = {"9", "10"}
            formats.update(item.get("numFmtId") for item in styles.iter()
                           if local_name(item.tag) == "numFmt" and "%" in item.get("formatCode", ""))
            cell_formats = next((item for item in styles if local_name(item.tag) == "cellXfs"), ())
            percentages = {str(index) for index, item in enumerate(cell_formats) if item.get("numFmtId") in formats}

        def value(cell):
            fields = {local_name(item.tag): item for item in cell}
            if cell.get("t") == "inlineStr" and "is" in fields:
                return rich_text(fields["is"])
            item = fields.get("v")
            if item is None or item.text is None:
                return "[Formula result not saved]" if "f" in fields else ""
            entry = item.text
            kind = cell.get("t")
            if kind == "s":
                if not re.fullmatch(r"\d{1,8}", entry) or int(entry) >= len(strings):
                    raise AttachmentError("The workbook contains an invalid shared text reference.")
                return strings[int(entry)]
            if kind == "b":
                return "TRUE" if entry == "1" else "FALSE"
            if kind in (None, "n") and cell.get("s") in percentages and len(entry) < 32:
                try:
                    number = Decimal(entry)
                    if number.is_finite() and abs(number.adjusted()) < 20:
                        return format((number * 100).normalize(), "f") + "%"
                except InvalidOperation:
                    pass
            return entry

        selected = []
        for item in book.iter():
            if local_name(item.tag) == "sheet":
                rel = next((entry for key, entry in item.attrib.items() if local_name(key) == "id"), None)
                if rel in targets:
                    selected.append((item.get("name", "Worksheet"), targets[rel]))
        if not selected:
            raise AttachmentError("The Excel workbook contains no readable worksheets.")

        cells = 0
        for name, target in selected[:MAX_SHEETS]:
            part = posixpath.normpath(target.lstrip("/") if target.startswith("/") else posixpath.join("xl", target))
            if part.startswith("../") or chr(92) in part or not part.endswith(".xml"):
                raise AttachmentError("The Excel workbook contains an unsupported worksheet reference.")
            sheet = xml(part)
            if not excerpt.add(f"Worksheet: {name}"):
                break
            for row in (item for item in sheet.iter() if local_name(item.tag) == "row"):
                values = []
                for cell in (item for item in row if local_name(item.tag) == "c"):
                    text = value(cell)
                    if text:
                        cells += 1
                        values.append(f"{cell.get('r', '?')}={text.replace(chr(10), ' ')}")
                if values and not excerpt.add(" | ".join(values)):
                    break
            if excerpt.truncated:
                break
            excerpt.add("")
        if cells == 0:
            raise AttachmentError("The Excel workbook has no populated cells.")
        excerpt.truncated |= len(selected) > MAX_SHEETS
        return excerpt.result()


if __name__ == "__main__":
    extension = sys.argv[1]
    try:
        raw = sys.stdin.buffer.read(INPUT_LIMIT + 1)
        if not raw or len(raw) > INPUT_LIMIT:
            raise AttachmentError("The document must be nonempty and 4 MB or smaller.")
        excerpt = Excerpt(int(sys.argv[2]))
        read = {".pdf": read_pdf, ".xlsx": read_workbook}[extension]
        print(json.dumps(read(raw, excerpt)))
    except ImportError:
        print(json.dumps({"error": "The local PDF library is missing from this Phoenix installation."}))
    except AttachmentError as error:
        print(json.dumps({"error": str(error)}))
    except Exception:
        print(json.dumps({"error": f"This {extension[1:].upper()} file couldn’t be decoded. Check that it opens normally."}))
