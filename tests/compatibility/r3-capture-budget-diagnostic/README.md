# R3 capture startup diagnostic

The original task 943 failed its fifth startup assertion, requiring the frozen synthetic fixture to yield at least one region through the production 55 ms deadline. This independent helper leaves that original failure and all production/test source unchanged.

It loads the pinned original `QingJie.Tests.exe` under its original identity. The diagnostic executable has a different assembly name. It calls the original synthetic constructor benchmark and fixture methods, reproduces the first four startup preconditions, then records the original three-argument `Read` result on a worker. Only after that call does it copy pixels, record color counts and pixel SHA, and save the synthetic PNG. The same frozen bitmap then passes through the existing explicit 2000 ms geometry entry point. That observation is functional geometry evidence only and cannot establish a 55 ms performance pass. No real desktop capture or visible/native test window is allowed.

`build.py --build <exact frozen R3 release-build> --output <fresh directory>` compiles only. A rebuild requires new output/plan hashes and independent review before VM execution. Never execute these helpers on the physical computer. `validation.json` records subsequent evidence separately from original 943.
