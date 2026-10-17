# 自动更新与发布

从 v1.1.4 起，设置页提供软件更新。自动检查默认开启，在程序启动时及每 6 小时检查一次；静默启动也会检查。发现新版本显示提示，下载完成后由用户确认更新并重启。检查或下载失败不会退出程序。

更新源固定为公开仓库 `https://github.com/Thz-zZ/codex`，读取最新的非预发布 Release。无须账号或内置 GitHub Token。仅推送代码或创建标签不会产生可用更新，Release 必须包含下述两个附件。

从 v1.1.6 起，GitHub API 限流、连接失败或暂时不可用时，会改用官方 `/releases/latest` 发布页的跳转地址取得版本号，再从对应 Release 下载 `update.json`。备用检查仍执行相同的版本、下载地址和安装包校验。清单中的可选 `notes` 字段用于在备用通道显示版本说明，由发布脚本从当前版本的 CHANGELOG 自动提取；旧清单没有此字段也可更新。

## 发布步骤

1. 修改 `src/AppVersion.cs` 中的 `Number`，例如 `1.1.6`，并更新版本记录和 README。
2. 在 Windows PowerShell 中执行 `./build-release.ps1`。工具链配置与 README 中的构建说明相同。
3. 脚本生成程序目录 `release/Codex Relay Lite v1.1.6/`，以及附件目录 `release/Codex Relay Lite v1.1.6-assets/`。
4. 在 GitHub 创建正式 Release，标签为 `v1.1.6`，填写更新说明，上传附件目录中的 `CodexRelayLite-1.1.6-windows-x64.zip` 和 `update.json`。
5. 发布后，旧版在自动检查或点击“检查更新”时会发现新版。建议发布前先在隔离目录验证更新包。

不要在生成 `update.json` 后修改 ZIP。清单中的下载文件名、版本、字节数和 SHA-256 必须与上传附件一致。ZIP 是完整的 Windows x64 程序包，不能使用 GitHub 自动生成的源码压缩包代替。

`update.json` 的格式如下，实际值由发布脚本生成：

```json
{
  "version": "1.1.6",
  "platform": "windows-x64",
  "url": "https://github.com/Thz-zZ/codex/releases/download/v1.1.6/CodexRelayLite-1.1.6-windows-x64.zip",
  "size": 12345678,
  "sha256": "64 位十六进制 SHA-256",
  "notes": "本次更新内容"
}
```

## 安装与恢复

主程序下载并验证更新包，将独立更新器复制到临时工作目录，然后完全退出。更新器等待进程退出，备份旧文件，替换程序并启动新版。新版界面成功读取本地配置后会确认启动成功；替换失败或新版无法启动时，更新器恢复旧文件并尝试重新打开旧版。

更新只管理发布包内的程序文件。中转配置和加密密钥仍保存在 `%APPDATA%\Codex Relay Lite`，Codex 配置和会话目录不会随程序更新被替换。会话或其他操作进行中时，需要完成后重新点击安装。

下载、备份和结果记录位于 `%LOCALAPPDATA%\Codex Relay Lite\updates\<任务 ID>\`。其中 `backup/` 保存该次更新前的程序文件，`result.json` 记录安装或回退结果。更新成功后删除下载的 ZIP，保留备份供排查使用。

程序目录需要当前用户可写；放在受保护目录时，请先移动完整程序文件夹。GitHub 访问失败可以稍后重试。校验使用 HTTPS 和 SHA-256，当前未接入代码签名或独立发布签名。

## 验证

原有 `--self-test` 已加入版本判断、更新清单、下载取消、损坏包、越界路径、重复文件、文件占用、配置保留、文件回退及隔离启动验证。`--smoke-test` 增加更新设置、下载进度、取消重试、确认重启和操作占用的界面测试。

第一次从 v1.1.3 或更早版本升级到支持自动更新的版本，仍需手动下载完整程序包。源码实现不会自动上传或发布 Release。
