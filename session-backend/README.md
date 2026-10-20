# 会话服务

`session_manager/` 中的数据读取、预览、删除、桌面目录清理及 ZIP 导入导出模块来自用户指定的 CodexSessionManager v1.1.1。`store.py` 增加了 zstandard 文本流的可寻址适配，修复压缩历史在本地分页预览时的兼容问题；其余引擎模块保留原始实现。原项目说明保留在 `UPSTREAM.md`，原项目未修改。

`service.py` 为 Relay Lite 提供私有标准输入/输出 JSON 通道，不开放端口。界面已整合到 Relay Lite，不依赖 Qt，也不需要用户安装 Python。

开发环境使用 Python 3.12+ x64，安装 `requirements-dev.txt` 后运行 `build.py` 生成 `build/current/SessionBackend.exe`，重复构建复用同一目录。使用 `python -m pytest tests -q` 验证隔离测试；测试不会修改真实会话。

删除与导入沿用原项目的 Codex 客户端占用检查；不会自动关闭其他程序。删除前展示完整范围并要求确认。导入不覆盖已有会话。版本校验、包哈希、路径检查和数据库事务保留在原模块中。
