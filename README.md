# Codex Relay Lite 源码

这个目录就是轻量版的完整源码项目：`src/` 放 C# 桌面程序，`ui-src/` 放界面文件，`session-backend/` 放随附的会话数据服务，`tests/` 放桌面界面测试。构建与运行均不依赖原 CodexSessionManager 项目目录。

## 构建

在 Windows PowerShell 中运行 `build-dev.ps1`。脚本使用 .NET Framework C# 编译器、WebView2 .NET SDK、Nett 和 Roslyn。默认从项目目录的上一级寻找 `native-toolchain/`；也可通过 `CODEX_RELAY_TOOLCHAIN` 指向依赖目录。WebView2 Runtime 需要安装在 Windows 上。

`CodexRelayLite.csproj` 可通过 MSBuild 属性 `RelayToolchainRoot` 指定同一依赖目录。

会话服务构建需要 Python 3.12+ x64，在独立虚拟环境安装 `session-backend/requirements-dev.txt`。通过 `CODEX_SESSION_PYTHON` 指定 Python 路径；`build-dev.ps1` 会自动调用 `build-sessions.ps1` 生成会话服务。发布后无需 Python 或 Qt。

## 版本与应用打包

当前版本为 **v1.1.9**，更新内容见 `CHANGELOG.md`。版本定义位于 `src/AppVersion.cs`，构建时自动同步到应用界面和 EXE 文件属性。

运行 `build-release.ps1` 生成可直接运行的 Windows x64 应用文件夹。可通过 `-OutputDirectory` 指定新输出目录。发布包包含应用与会话服务 EXE、DLL、界面、许可、会话管理说明及版本记录。

自动更新发布步骤见 [UPDATE-GUIDE.md](UPDATE-GUIDE.md)。程序默认从 `Thz-zZ/codex` 的 GitHub 正式版检查更新；发布脚本同时生成完整 ZIP 和 `update.json`。

本机程序正在运行时，可用 `build-dev.ps1 -OutputDirectory build/update-dev` 在独立目录编译；对应打包命令为 `build-release.ps1 -BuildDirectory build/update-dev`。新目录也需要先构建或复制未变更的 `SessionBackend.exe`。

## 内容

- `src/`：原生窗口、托盘、配置存储、NewAPI/Sub2API 查询和本地设置。
- `ui-src/`：中转列表、详情、缓存图表、编辑表单和样式。
- `session-backend/`：来自 CodexSessionManager v1.1.1 的本地会话引擎、私有 JSON 桥接及隔离测试。
- `tests/`：隔离的模拟接口与 smoke 测试。
- `THIRD-PARTY-NOTICES.txt`：随附组件和代码的许可说明。

发布的源码压缩包不包含生成的 EXE、真实 Codex 配置、API 密钥、本机缓存或运行时依赖包。在本地构建或测试后，项目内会生成 `build/` 和 Python 缓存目录。

## 上传 GitHub

根目录的 `.gitignore` 已排除 `build/`、编译输出、Python 缓存与虚拟环境。源码、界面、构建脚本、测试和第三方许可仍可正常提交。

忽略规则不会删除磁盘上的文件，因此 VS Code 资源管理器中仍可能看到 `build/`；它不会被 `git add .` 加入提交。可运行 `git status --short --ignored` 查看，`!! build/` 表示已忽略。

会话使用说明见 `SESSION-GUIDE.md`。导入与永久删除需要关闭其他 Codex 客户端；不会自动关闭这些客户端。

## 验证

使用会话虚拟环境运行 `python -m pytest session-backend/tests -q`。桌面测试先运行 `python tests/seed-session-fixture.py` 创建 build 内的合成会话，再执行 `build/Codex Relay Lite.exe --smoke-test`；运行 `node tests/generate-fixtures.cjs` 后，可通过 `--self-test` 验证原有中转逻辑。期望值来自独立旧版参考快照，生成时仅平移日期，无需其他项目目录。测试不操作真实用户会话。
