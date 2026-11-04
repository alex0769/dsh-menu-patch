# DeepSeekHarnessMenuPatch

一键解锁 DeepSeek Harness 桌面版里被 `development` 条件隐藏的两个菜单项：**刷新页面**、**重启应用与 Host**。

桌面版每次自动更新都会把 `resources\app.asar` 换回官方版本，补丁随之失效；升级后双击本程序再重启一次客户端即可。

## 用法

1. **完全退出 DeepSeek Harness**（托盘图标也要退出）——不退出也能打补丁，但要退出重开才生效。
2. 双击 `DeepSeekHarnessMenuPatch.exe`（默认自动找到 `%LOCALAPPDATA%\Programs\DeepSeek Harness\resources\app.asar`）。
3. 看到 `[成功]` 后按回车关闭，重新启动 DeepSeek Harness。

命令行用法：

```
DeepSeekHarnessMenuPatch.exe                     # 安装补丁（自动定位 app.asar）
DeepSeekHarnessMenuPatch.exe --asar "D:\...\resources\app.asar"
DeepSeekHarnessMenuPatch.exe --dry-run           # 只检查，不写
DeepSeekHarnessMenuPatch.exe --revert            # 还原成官方原样
DeepSeekHarnessMenuPatch.exe --backup            # 写入前备份 app.asar.bak-时间戳（约 180MB）
DeepSeekHarnessMenuPatch.exe --help
```

可重复运行：已打过补丁时会提示跳过，不会重复修改。

## 做了什么

`lib/main.js` 的菜单模板里：

```js
...development ? [
  { type: "separator" },
  { label: currentDesktopLocale().messages.reloadPageMenu, role: "reload" },
  { label: currentDesktopLocale().messages.restartAppHostMenu, click: () => { ... } }
] : [],
```

`development` 就是 `!app.isPackaged`，打包版恒为 `false`，整段被丢弃。工具把它等长替换成 `true||false`（都是 11 字节），条件恒真，两个菜单项进入模板。

同时 asar 头部对每个文件带 SHA256 完整性记录，工具会重算并同步更新 `lib/main.js` 的 `integrity.hash` 与 `integrity.blocks[0]`（64 字符等长替换）。因此 asar 内部所有偏移、长度、pickle 字段都保持原样，不需要重打包。

## 安全设计

- **不改结构**：只替换 11 字节 + 等长 hash；写入前校验 asar 头部格式、`lib/main.js` 偏移范围、当前内容哈希与头部记录一致。
- **上下文校验**：命中的 `development ? [` 前后必须能找到 `reloadPageMenu` / `restartAppHostMenu`，否则拒绝修改（防止未来版本结构变化时误伤）。
- **唯一性校验**：`development ? [` 必须恰好出现 1 次；头部里旧 hash 必须恰好出现 2 次；否则中止，不写任何字节。
- **写后复核**：写完重新读取并重算哈希，与头部记录比对，失败会明确报错。
- **可还原**：`--revert` 就是反向替换 + 重算哈希，还原后与官方文件逐字节一致。
- **幂等**：重复运行只提示跳过。

## 已知限制

- 只支持 `lib/main.js` 的完整性记录为**单块**（文件 < 4MB）的情况；当前版本 `lib/main.js` 约 505KB。若未来版本跨块，工具会报错中止而不会写坏文件。
- 需要 .NET Framework 4.x 运行时（Windows 10/11 自带，无需安装）。
- 程序没有数字签名，个别杀毒软件可能误报；源码 `DeepSeekHarnessMenuPatch.cs` 就在同目录，可自行审阅/编译。
- 若安装在 `Program Files` 下，需要对 exe 右键 → 以管理员身份运行。

## 免责声明

本工具不是 DeepSeek Harness 官方发布物，与官方无任何关联。它只修改你本机安装目录里的 `resources\app.asar`，不联网、不收集任何数据；修改第三方软件请自行判断并承担风险，建议先用 `--backup` 留一份原文件（随时可用 `--revert` 还原）。本仓库不包含任何官方程序文件。

## 许可证

MIT，见同目录 [LICENSE](LICENSE)。Copyright (c) 2026 alex0769。

## 自行编译

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /optimize+ /codepage:65001 ^
  /r:System.Web.Extensions.dll /out:DeepSeekHarnessMenuPatch.exe DeepSeekHarnessMenuPatch.cs
```

源码是 UTF-8；`/codepage:65001` 保证在默认代码页不是 UTF-8 的机器上编译时中文字符串不会乱码。
