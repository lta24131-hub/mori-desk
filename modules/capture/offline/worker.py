"""One CPU-only translation job. No network, clipboard, text files or persistent model."""
import json
import os
from pathlib import Path
import re
import sys

sys.dont_write_bytecode = True
ROOT = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(ROOT / "runtime" / "packages"))


def supported(text):
    # This bilingual pack is deliberately not advertised as a language detector.
    return all(not c.isalpha() or "A" <= c <= "Z" or "a" <= c <= "z"
               or "\u3400" <= c <= "\u9fff" for c in text)


def translate(texts, target):
    import ctranslate2
    import sentencepiece
    if target not in ("en", "zh") or not isinstance(texts, list) or not 0 < len(texts) <= 500:
        raise ValueError("Invalid request")
    if any(not isinstance(t, str) or not t.strip() or len(t) > 10000 for t in texts) or sum(map(len, texts)) > 50000:
        raise ValueError("Text too large")
    if any(not supported(t) for t in texts):
        raise ValueError("Only Chinese and English are supported offline")
    direction = "zh_en" if target == "en" else "en_zh"
    model = ROOT / "models" / direction
    sp = sentencepiece.SentencePieceProcessor(model_file=str(model / "sentencepiece.model"))
    pending, locations, layouts = [], [], []
    for i, text in enumerate(texts):
        layout = []
        for segment in re.split(r"(\r\n|\r|\n)", text):
            if not segment or segment.isspace() or not re.search(r"[\u3400-\u9fff]" if target == "en" else r"[A-Za-z]", segment):
                layout.append(segment)
                continue
            # Keep complete sentences where possible; never silently truncate long input.
            sentences = re.split(r"(?<=[。！？])|(?<=[.!?])\s+", segment)
            for sentence in sentences:
                if not sentence.strip():
                    continue
                tokens = sp.encode(sentence, out_type=str)
                for offset in range(0, len(tokens), 192):
                    locations.append((i, len(layout)))
                    layout.append(None)
                    pending.append(tokens[offset:offset + 192])
        layouts.append(layout)
    if pending:
        engine = ctranslate2.Translator(str(model / "model"), device="cpu", compute_type="int8",
                                       inter_threads=1, intra_threads=min(4, os.cpu_count() or 1))
        results = engine.translate_batch(pending, beam_size=4, max_batch_size=8, batch_type="examples",
                                         max_input_length=192, max_decoding_length=512, replace_unknowns=True, length_penalty=0.2)
        for (i, j), result in zip(locations, results):
            tokens = result.hypotheses[0]
            if not tokens or len(tokens) >= 512:
                raise ValueError("Translation incomplete; split the text into smaller paragraphs")
            layouts[i][j] = sp.decode(tokens).replace("\u2581", " ").strip()
        del engine
    outputs = []
    for layout in layouts:
        joined = ""
        for part in layout:
            if part is None:
                raise ValueError("Missing translation")
            if target == "en" and joined and part and not joined[-1].isspace() and not part[0].isspace():
                joined += " "
            joined += part
        outputs.append(joined)
    return outputs


if __name__ == "__main__":
    # Even an abruptly closed parent cannot leave a forgotten model process.
    import threading
    watchdog = threading.Timer(95, lambda: os._exit(3))
    watchdog.daemon = True
    watchdog.start()
    try:
        raw = sys.stdin.buffer.read(400001)
        if len(raw) > 400000:
            raise ValueError("Request too large")
        request = json.loads(raw.decode("utf-8-sig"))
        answer = translate(request["texts"], request["target"])
        sys.stdout.buffer.write(json.dumps({"translations": answer}, ensure_ascii=False).encode("utf-8"))
    except Exception as exc:
        # Do not log source text or model paths. The parent supplies localized guidance.
        sys.stdout.buffer.write(json.dumps({"error": type(exc).__name__}).encode("utf-8"))
        sys.exit(1)
