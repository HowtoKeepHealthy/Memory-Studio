# 本地测试和演示进程

`native_integration.cpp` 通过 `LoadLibraryW` / `GetProcAddress` 使用公开 C ABI，扫描当前测试进程自己分配的、限定地址范围的内存页，无需管理员权限。测试进程不会修改其他应用。

覆盖内容：

- Int32 首次精确扫描、地址范围的开闭边界、结果分页。
- 再次筛选：变化、未变化、增加、减少，以及未知初始值和连续快照。
- Float32 / Float64、自然对齐和非对齐数值扫描。
- UTF-8 中文、UTF-16 中文、字节序列，跨 64KB / 256KB / 1MB 读取边界。
- 数值读写、无效地址、只读页、不可访问页和已退出进程。
- 结果上限、无效请求和取消扫描时保留旧结果及其比较快照。
- 结果数恰好等于上限时扫描成功。

取消测试使用 128MB 内存并在后台扫描；看到公开进度接口的 `running` 状态后请求取消，不靠固定休眠猜测扫描时间。测试结束自动释放内存。已退出进程测试只创建并终止它自己的隐藏子进程。

从项目根目录在 PowerShell 中运行，编译器由项目提供：

```powershell
& .\.tools\llvm-mingw\bin\clang++.exe -std=c++20 -O2 -static -municode .\tests\native_integration.cpp -o .\artifacts\native\native_integration.exe
& .\.tools\llvm-mingw\bin\clang++.exe -std=c++20 -O2 -static .\tests\DemoTarget.cpp -o .\artifacts\native\DemoTarget.exe
& .\artifacts\native\native_integration.exe .\artifacts\native\memory_core.dll
```

先构建 `memory_core.dll`；测试失败返回退出码 1，无法装载 DLL 或初始化失败返回 2，全部通过返回 0。

运行根目录 `build.cmd -Test` 会执行原生基础/扫描扩展，以及应用内的 `SmokeTests` / `FeatureSmokeTests` / `EnhancedSmokeTests`，报告写入 `artifacts/managed-test.txt`。这些检查覆盖真实 WPF ViewModel 扫描、实时结果更新、扫描基准撤销、批量修改撤销、跨浏览器冻结值同步、进制转换、浮点容差、.CT 数据往返、混合类型批量验证、跨页全选，以及 x86/x64 指令解码和不可读页边界。

独立专项测试（先运行 build.cmd）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-process-tools.ps1
powershell -ExecutionPolicy Bypass -File scripts/test-browser-windows.ps1
powershell -ExecutionPolicy Bypass -File scripts/test-browser-windows.ps1 -HistoryOverlapOnly
powershell -ExecutionPolicy Bypass -File scripts/test-wpf-interface.ps1
```

进程工具专项包含 32 个真实 x64/WOW64 暂停恢复检查与 24 个模块指针链扫描检查；浏览窗口专项包含 36 个真实 WPF 交互检查，验证指令中心定位、滚轮上下加载、HEX/代码/NASM 补丁、来源跳转、主窗口不被追踪阻塞及退出时安全脱离；重叠撤销专项包含 4 个失败保留/重试检查；主界面专项包含 75 个多选、双击批量编辑、真实内存工具栏写入、紧凑布局和缩放检查。源码位于 `tests/process_control`、`tests/browser_windows` 和 `tests/wpf_interface`，输出、fixture、临时项目和报告均在 `artifacts` 中。

访问来源测试会启动隐藏的自建 `DemoTarget.exe --trace-test` 子进程，检查准确读写指令、同页相邻地址过滤、重复命中聚合、停止后页面保护恢复及目标继续执行、再次附加写入追踪，以及目标退出后会话释放。测试只终止它自己创建的子进程。窗口拾取测试使用测试窗口的 HWND 验证 PID、子窗口归属与无效目标过滤。

`DemoTarget.exe` 是供界面实际附加的演示目标，显示 PID 和可搜索字段地址。初始 Health 为 Int32 `100`、Gold 为 Int32 `2500`、Speed 为 Float32 `1.25`，这些值只通过键盘操作改变；Tick 每秒变化。

```powershell
& .\artifacts\native\DemoTarget.exe
```

按 `h` 减少 10 点 Health，按 `g` 增加 100 Gold，按 `s` 增加 0.25 Speed，按 `r` 重置这三个字段，按 `p` 打印当前数值，按 `q` 退出。应用中的“启动演示”会打开该控制台并附加。

建议手动验收：启动演示 → 选择 Int32 和精确值 `100` → 首次扫描 → 在演示控制台按 `h` → 选择“减少”再次扫描 → 添加 Health 地址 → 写入 `150` → 控制台按 `p` 确认。勾选冻结后再按 `h`，稍等并按 `p` 确认数值恢复到冻结值；最后取消冻结。
