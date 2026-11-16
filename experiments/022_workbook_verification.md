# Experiment 022 — Four workers and workbook verification

The fictional NileGate Foods case tests whether four local workers can draft an investment recommendation from a sponsor PDF and an Excel workbook. The app checks their consolidated draft against calculations run directly from the attachments.

## Reproduce

Open Phoenix AI on Windows and start a new chat. Attach the [sponsor brief](fixtures/cold-storage/01-sponsor-brief.pdf) and [workbook](fixtures/cold-storage/02-project-inputs.xlsx). Send the [workers-first prompt](fixtures/cold-storage/prompt.txt). Use Smart Auto and Auto hardware. Open the final reply’s Details to check the assigned workers, the draft, and the CPU verification. The scheduler can reduce the worker count if there is insufficient memory.

## Observed run

- The D1 router timed out. Smart Auto assigned LFM3 1.7B to the dGPU and iGPU, Qwen 3.5 2B to the CPU, and LFM2.5 230M to the NPU.
- The lead model repeatedly reached its output budget while combining the drafts. The run finished in 57 seconds with one reply and no manual continuation.
- None of the 22 required rows matched the verifier. This count includes missing and differently formatted rows. The saved report does not preserve the worker draft, so the run cannot separate those cases from incorrect calculations.
- The calculator filled or corrected the 22 checks. A second calculation from the attachments agreed. The final recommendation was Option A, with an EGP 30.24 million base-case present-value cost. Raising Option B’s upfront cost by 20% took it to EGP 12.78 million, above the EGP 12.50 million cap.

The result demonstrates automatic correction and shows that parallel workers can exhaust their combined answer budget. The larger lead context and section-boundary continuation were developed afterward. This run predates those changes.
