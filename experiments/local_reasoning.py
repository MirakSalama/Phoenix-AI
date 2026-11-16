"""Separate model-specific reasoning tokens from the reply sent to chat and voice."""


class ReasoningReply:
    def __init__(self, mode, marker_id, decode, on_text):
        if mode == "lfm3":
            start, end = "<think>", "</think>"
            self.inside = True  # The LFM3 generation prompt opens the thought.
        elif mode == "gemma":
            start, end = "<|channel>", "<channel|>"
            self.inside = False
        else:
            raise ValueError("Thinking mode is unavailable for this model.")
        self.start, self.end = marker_id(start), marker_id(end)
        if self.start is None or self.end is None:
            raise ValueError("The local tokenizer is missing its thinking markers.")
        self.decode, self.on_text = decode, on_text
        self.answer_ids = []
        self.shown = ""
        self.thinking_tokens = 0

    def add(self, token):
        if token == self.start:
            self.inside = True
        elif token == self.end:
            self.inside = False
        elif not self.inside:
            self.answer_ids.append(token)
            if self.on_text:
                text = self.decode(self.answer_ids).lstrip()
                if text and text != self.shown and not text.endswith("\ufffd"):
                    replace = not text.startswith(self.shown)
                    self.on_text(text if replace else text[len(self.shown):], replace)
                    self.shown = text
            return
        self.thinking_tokens += 1

    def result(self):
        answer = self.decode(self.answer_ids).strip()
        if not answer:
            raise ValueError("Thinking ended before a final answer. Try a shorter prompt or turn Thinking off.")
        return {"response": answer, "thinking_tokens": self.thinking_tokens}
