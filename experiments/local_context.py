"""The input and output of a turn share the backend's loaded context."""


def assistant_prefix(messages):
    """Split an unfinished assistant turn from the messages that open it."""
    if messages and messages[-1]["role"] == "assistant":
        return messages[:-1], messages[-1]["content"]
    return messages, ""


def context_reserve(capacity):
    return min(128, max(16, capacity // 32))


def response_budget(capacity, prompt_tokens, requested):
    remaining = capacity - prompt_tokens - context_reserve(capacity)
    if remaining < 1:
        raise ValueError(f"The prompt uses {prompt_tokens:,} tokens and exceeds the available context "
                         f"of {capacity:,} tokens with room for an answer.")
    return remaining if requested <= 0 else min(remaining, requested)


def generation_stop(capacity, prompt_tokens, budget, generated, ended=False):
    if ended or generated < budget:
        return "eos"
    return "context" if prompt_tokens + budget >= capacity - context_reserve(capacity) else "limit"
