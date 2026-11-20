# OCR vocabulary regression

These two frozen text fixtures contain recognized words, model confidence and boxes, not screenshots. The short card exercises a genuinely ambiguous `Al`; the mixed bilingual test extract distinguishes a person's name, AI, an organization and an audio term. Only the fields used by the test are included; there are no local paths or run metadata.

1. Build Nook with the repository's `build.ps1`. This checks that the checked-in compact vocabulary is the exact projection of the upstream list.
2. Run `modules/capture/tests/Build-OcrLexiconTests.ps1 -ApplicationBuild <build-folder> -TestDirectory <new-test-folder>`. It stages and compiles, but does not run anything. Only this **test** folder includes the full upstream dictionary as `frequency-full-reference.txt`.
3. Copy the test folder into `C:\NookLab\Audit\apps\<case>\` in the dedicated VM and run `QingJie.Tests.exe` as `NookTester`. Exit code 0 and `"passed":true` in `lexicon-tests.json` indicate success.

The independent oracle rebuilds the original full lookup from the reference, checks original term frequencies and insertion order, and compares corrections for every possible spelling within one accepted visual substitution of a reviewed term. Ordinary low-confidence prose, untouched names, protected text and dense mixed text are tested separately. These tests do not invoke OCR, the network or the installed application.

The regression `AI model 12: keep all files.` covers two independent failures: tiny type may be recognized as `Al`, and a translator may drop the entire leading identifier. The original transcript is never changed. A model-scored, spatially aligned, numbered software header can accept an independent local reading of the original pixels; names, materials, `A1`, unnumbered prose, unknown/user-derived words, ambiguous ownership, or a failed check must not infer `AI`. If a translated header has lost the identifier but still has exactly the matching number and noun, the original or verified spelling is restored. The lexical runner injects the secondary evidence; true OCR, displayed placement, and service timing need a separate VM test on a rendered 11-pixel footer.
