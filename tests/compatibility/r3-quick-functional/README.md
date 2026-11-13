# R3 quick conversion functional supplement

The original task 931 failed before invoking tests due to an ambiguous reflection overload. Task 1005 selected the exact original method, then failed the ordinary 20-second test deadline on the first JPG capabilities request. Both failures remain recorded.

This separate runner invokes the frozen four-argument `RunReal` with its existing phase tracing and explicit VM-only 120-second test wait. It keeps every original result, format, source hash, metadata, temporary-session and lock-release assertion. The production 45-second capability timeout and the original outer 600-second bound are unchanged. This cannot pass the ordinary 20-second performance criterion. Logs must retain phase timings and any functional failure.

The runner and wrapper require the dedicated QEMU/NookTester VM. No product or worker is executed on the physical computer. `build.py` only compiles reviewed sources; new output requires new provenance and reviewed package bindings. No binary, personal library, credential or raw VM log is included here. `validation.json` will contain the actual returned scope/result.
