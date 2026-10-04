# WPF 交互专项测试

先执行项目的 `build.cmd`，再从仓库根目录执行：

```powershell
./scripts/test-wpf-interface.ps1
```

脚本会复制应用源码到独立的 `artifacts/wpf-interface/run-*` 目录，在其中构建和运行真实 WPF 窗口。它不依赖生产 app 的 obj/bin，也不加入普通 `build -Test`。

覆盖三表多选、右键快照、按列双击批量编辑、真实自进程 1,024 个结果跨页全选、可见结果范围、工具栏写入选组、780×520 布局、设置折叠、150% 缩放和字体实时更新。测试仅向自己分配的内存写入；编辑弹窗会自动取消，窗口位于屏幕之外。

每次运行生成 `wpf-interface-results.txt` 和五张截图：1280 主界面、780 主界面、780 扫描设置展开、780 地址表、780 在 150% 缩放与字体 18 下的主界面。设置检查结束后恢复原来的缩放和字体；窗口尺寸记忆在本测试中关闭。

可使用 `-NativeDllPath <路径>` 指定已构建的引擎；默认读取 `artifacts/native/memory_core.dll`。本测试还需要项目本地 .NET SDK、`artifacts/native/nasm.exe` 和 `DemoTarget.exe`。
