# R3 quick conversion reflection compatibility supplement

Task 931 failed in the external runner before calling the product tests: `GetMethod("RunReal", flags)` became ambiguous when a four-argument tracing overload was added. The source under `original/` preserves that runner. The reviewed supplement changes only the lookup to the exact original `RunReal(string, string, Action<bool,string>)` signature. It calls the same original three-argument method and assertions, with the original 600-second outer limit. The product, source tests and failed report are not changed.

The wrapper has a fresh name, a single permitted plan label, and a unique runner filename so no earlier deployed helper is overwritten. It retains the existing dedicated-VM, pinned-product, owned-root and kill-on-close Job checks.

`build.py --build <frozen-R3-release-build> --output <new-directory>` only compiles after checking the reviewed source and reference hashes. New binaries need fresh provenance, plan hashes, package review and dedicated VM deployment; never run helpers on the physical computer or replace historical evidence. Compiler success is not a product test pass. `validation.json` tracks the returned supplement result; task 931 remains failed.
