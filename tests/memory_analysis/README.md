# 地址分析专项

先在根目录构建现有本机依赖，再执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-memory-analysis.ps1
```

脚本把应用及测试源码复制到独立的 `artifacts/memory-analysis/run-*`，只在 artifacts 中构建和输出报告、Markdown/JSON 样例及深色/纸白窗口截图。测试不调用 AI、上传数据或操作第三方进程。

真实内存 fixture 验证数据值、模块 RVA 和范围边缘、数据解码不确定性、Iced 寄存器及内存读写宽度、LEA 语义、中文作用解释、相对调用目标、捕获证据快照和 PID/范围过滤、64 位 JSON 精度、Markdown 转义、读取失败及 modeless 窗口复制/主题切换/会话释放。捕获 DTO 在此 fixture 中构造以精确核对；真正 PAGE_GUARD 捕获后打开分析窗口由 browser_windows 专项覆盖。
