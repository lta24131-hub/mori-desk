"""Synthetic text only; measures the optional runtime, not the tray process."""
import ctypes
from ctypes import wintypes
import importlib.util
import json
from pathlib import Path
import sys
import time

source = Path(__file__).with_name("worker.py")
spec = importlib.util.spec_from_file_location("worker", source)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

for target, texts in [
    ("en", ["请确认交货日期，谢谢。", "保存图片", "复制文字", "这个零件需要改成黑色，交货时间是下周五。", "Hello\n请发报价单。"]),
    ("zh", ["Please confirm the delivery date. Thank you.", "Save image", "Copy text", "Fold the tripod before putting it in the bag."])
]:
    start = time.perf_counter()
    translated = worker.translate(texts, target)
    print(json.dumps({"target": target, "ms": round((time.perf_counter()-start)*1000), "results": translated}, ensure_ascii=True))

class Memory(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [(key, ctypes.c_size_t) for key in (
        "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage",
        "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage")]
memory = Memory()
memory.cb = ctypes.sizeof(memory)
ctypes.windll.kernel32.GetCurrentProcess.restype = wintypes.HANDLE
ctypes.windll.psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Memory), wintypes.DWORD]
ctypes.windll.psapi.GetProcessMemoryInfo(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(memory), memory.cb)
print("peak_private_mb=", round(memory.PeakPagefileUsage / 1048576, 1))
