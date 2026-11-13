# R3 capture fallback/lifecycle supplement

Original task 943 remains failed: its synthetic fixture returned no region within the unchanged production 55 ms budget. Diagnostic 1091 found a real first-call timeout and successful warmer explicit-budget geometry; neither establishes the original timing requirement.

This independent helper checks user-visible fallback behavior and asynchronous lifecycle only. The actual existing zero-budget reader produces an empty timed-out result, then the fixture injects that result to verify native-target click and exact manual crop remain available. The actual production `PrepareVisualTargets` with unchanged 55 ms checks pending-worker reuse, completion without hover/crop replacement, completed-result reuse, committed manual crop preservation, pre-cancellation and the original close-pending assertions. No visible/native window, capture, hotkey, tray or mouse capture is used.

`build.py` compiles only against exact frozen R3 references. Runtime reconstruction needs the pinned original capture test assembly and a fresh reviewed package. Do not run helpers/product on the physical host. Results and original failures remain separate in `validation.json`.

Task 1229 passed all 24 independent functional checks. The owned process Job was empty and no watchdog fired. Actual production-budget hover work still reported TimedOut, so this is deliberately not a 55 ms performance pass; task 943 remains failed.
