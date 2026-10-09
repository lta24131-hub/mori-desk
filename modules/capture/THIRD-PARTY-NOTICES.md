# Local OCR dependencies

- Tesseract .NET wrapper 5.2.0, Copyright Charles Weld. Apache License 2.0. https://github.com/charlesw/tesseract
- Tesseract OCR 5, Apache License 2.0. https://github.com/tesseract-ocr/tesseract
- English tessdata_fast LSTM model (bundled) and optional Korean, Japanese, Russian, French, German, Spanish, Portuguese, Italian, Arabic, Thai and Vietnamese models (downloaded on request), Apache License 2.0. https://github.com/tesseract-ocr/tessdata_fast (revision 87416418657359cb625c412a48b6e1d6d41c29bd; all covered by the license text in licenses/English-model.txt)
- Leptonica 1.82.0, BSD-style license. https://github.com/DanBloomberg/leptonica

These unmodified components are distributed in Tesseract.dll, x64/, and tessdata/. Full license texts are in licenses/. No user screenshots are bundled with the software.

## Optional offline translation (not bundled in the base download)

Downloaded only when the user requests the Chinese-English pack. Exact URLs, versions, sizes and SHA-256 hashes are pinned in `offline/catalog.json`. No PyTorch, Stanza or full Argos application is installed. Inference is CPU-only.

- CPython embedded 3.13.12: Python Software Foundation License and included third-party notices. The archive retains `runtime/LICENSE.txt` (including notices for its standard runtime components).
- CTranslate2 4.8.2: Copyright SYSTRAN and The OpenNMT Authors, MIT. Full text in `licenses/CTranslate2.txt`. Official Windows wheel also contains Intel numerical/OpenMP libraries and NVIDIA CUDA/cuDNN support components; their upstream vendor terms apply. These are fetched directly from the upstream PyPI wheel, not included in QingJie's base ZIP. Inference does not use GPU. Upstream build provenance: https://github.com/OpenNMT/CTranslate2/tree/v4.8.2/python/tools . Intel terms: https://www.intel.com/content/www/us/en/developer/articles/license/end-user-license-agreement.html . NVIDIA terms: https://docs.nvidia.com/deeplearning/cudnn/backend/latest/reference/eula.html .
- SentencePiece 0.2.2: Copyright Google, Apache License 2.0. Full text in `licenses/SentencePiece.txt`.
- NumPy 2.5.3: NumPy Developers, BSD-3-Clause, with additional bundled dependency notices retained under `runtime/packages/numpy-2.5.3.dist-info/licenses`.
- PyYAML 6.0.3: MIT, retained under `runtime/packages/pyyaml-6.0.3.dist-info/licenses`.
- Argos English→Chinese and Chinese→English 1.9 model packages: derived from OPUS-MT, CC BY 4.0. Authors Jörg Tiedemann and Santhosh Thottingal, “OPUS-MT — Building open translation services for the World,” EAMT 2020, Lisbon, Portugal. Source packages from https://argos-net.com/ (catalog https://github.com/argosopentech/argospm-index). Model weights and tokenizer are unmodified; QingJie omits unused Stanza files and uses its own sentence splitting / region batching. Attribution READMEs remain alongside both models. Full license in `licenses/Offline-models-CC-BY-4.0.txt`.

Tencent and Google translation services are external services, not bundled open-source components. Their service terms and availability apply; no service guarantee or affiliation is implied.
