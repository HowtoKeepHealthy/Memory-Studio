# 浏览窗口专项集成

在 Windows x64 上，先执行根目录 `build.cmd`，再运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-browser-windows.ps1
```

脚本将 app 源码复制到每次运行独立的 `artifacts/browser-windows/run-*`（排除 bin/obj），链接这些源码编译 WPF harness。只在 artifacts 写入构建结果、报告和截图，不改生产 app 的 obj/bin，也不加入常规 `build -Test`。

测试会短暂显示真实窗口，启动自建 DemoTarget 子进程，并在测试进程中分配内存。41 项覆盖：指定 IP 居中选中；不确定反向边界标记；可靠锚点下的可变长度解码；连续 prepend/append、滚动锚点及实际滚轮事件；NASM 指令与相对跳转；RX 页 NOP/撤销；HEX 实际字节编辑/撤销；来源行真实双击后安全停止追踪、保留结果、打开代码与数据窗口及会话释放；从实际追踪或主表分析地址时自动安全停止、保留并导出真实捕获证据、读取恢复、modeless 窗口及独立会话释放；MainVM modeless 行为、无关页监视读写、guard 页冻结跳过、停止后恢复监视、主窗口关闭前恢复追踪。

每次运行生成 `browser-window-results.txt` 和三张窗口 PNG。测试进程会释放所分配内存，终止自己的 DemoTarget；不扫描或修改其它程序。

开发时可以指定已经构建好的隔离原生 DLL：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-browser-windows.ps1 -NativeDllPath artifacts/native-expanded-check/memory_core.dll
```

项目也可以直接运行，默认链接仓库 app 源码；推荐用上面的脚本，避免测试与正在编辑的源码混用。`RepositoryRoot`、`AppSourcePath`、`NativeDllPath` 三个 MSBuild 属性可显式覆盖资源和源码路径。

仅验证重叠修改的部分撤销失败及重试，可以单独运行，不打开窗口，也不重跑上面的 41 项：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-browser-windows.ps1 -HistoryOverlapOnly
```

这 4 项在测试进程的两个页面上构造两处重叠修改和一处独立修改，再将重叠页设为只读：确认独立步骤正常恢复、失败重叠步骤与更早依赖步骤保留，重新开放写权限后重试恢复原值，并确认冻结维护使用恢复后的基准。

真实向上滚轮回归（用 Win32 SendInput 经 WPF 输入队列，而非直接触发路由事件）可单独运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-browser-windows.ps1 -ScrollOnly
```

该专项通过主窗口记录的实际反汇编操作打开浏览器，验证当前 IP 接近可读页起点时保留可读的前方字节、在不可读边界准确停止、边界变为可读后同一滚轮动作重试加载并可见上移，以及连续快速滚轮进入两个反向加载区域。测试结束会恢复鼠标位置，并生成 `disassembly-real-upward-wheel.png`。
