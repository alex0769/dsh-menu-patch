// DeepSeekHarnessMenuPatch - 为 DeepSeek Harness 桌面版解锁“刷新页面 / 重启应用与 Host”两个菜单项。
//
// 原理：安装目录 resources\app.asar 内 lib/main.js 的菜单模板里有
//     ...development ? [ ...重新加载菜单项... ] : [],
// 而 development = !app.isPackaged，打包版恒为 false，整段被丢弃。
// 本工具把该处的 development 等长替换为 true||false（11 字节 -> 11 字节），
// 保持 asar 内所有偏移不变，并同步更新 asar 头部里该文件的 SHA256 完整性记录。
//
// 编译：csc /nologo /target:exe /optimize+ /r:System.Web.Extensions.dll /out:DeepSeekHarnessMenuPatch.exe DeepSeekHarnessMenuPatch.cs
// 目标框架：.NET Framework 4.x（Windows 自带），C# 5 语法。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

internal static class Program
{
    private const string ToolVersion = "0.1.0";
    private const string DevToken = "development";          // 11 字节
    private const string PatchToken = "true||false";        // 11 字节
    private const string InstallPrefix = "development ? [";
    private const string PatchedPrefix = "true||false ? [";

    private static int Main(string[] args)
    {
        bool pause = args.Length == 0 || HasArg(args, "--pause") || HasArg(args, "-p");
        if (HasArg(args, "--no-pause") || HasArg(args, "-n")) pause = false;

        int code = 1;
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            code = Run(args);
        }
        catch (ToolException ex)
        {
            WriteError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteError("没有写入权限：" + ex.Message);
            Console.WriteLine("      如果 DeepSeek Harness 装在 Program Files 下，请右键本程序 → 以管理员身份运行。");
        }
        catch (IOException ex)
        {
            WriteError("读写 app.asar 失败：" + ex.Message);
            Console.WriteLine("      如果 DeepSeek Harness 正在运行，请先完全退出（含托盘图标）再试。");
        }
        catch (Exception ex)
        {
            WriteError(ex.ToString());
        }

        if (pause)
        {
            Console.WriteLine();
            Console.Write("按回车键退出...");
            Console.ReadLine();
        }
        return code;
    }

    private static int Run(string[] args)
    {
        bool revert = false, backup = false, dryRun = false, force = false;
        string asarArg = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string lower = a.ToLowerInvariant();
            switch (lower)
            {
                case "-h":
                case "--help":
                case "/?":
                case "-?":
                case "help":
                    PrintHelp();
                    return 0;
                case "-r":
                case "--revert":
                case "revert":
                    revert = true;
                    break;
                case "-b":
                case "--backup":
                case "backup":
                    backup = true;
                    break;
                case "-d":
                case "--dry-run":
                case "dry-run":
                    dryRun = true;
                    break;
                case "-f":
                case "--force":
                case "force":
                    force = true;
                    break;
                case "-n":
                case "--no-pause":
                case "no-pause":
                case "-p":
                case "--pause":
                case "pause":
                    break; // 由 Main 统一处理暂停逻辑
                case "-a":
                case "--asar":
                case "asar":
                    if (i + 1 >= args.Length) throw new ToolException("--asar 后面需要 app.asar 的完整路径");
                    asarArg = args[++i];
                    break;
                default:
                    if (!a.StartsWith("-") && asarArg == null) asarArg = a;
                    else throw new ToolException("无法识别的参数：" + a + "（用 --help 查看用法）");
                    break;
            }
        }

        Console.WriteLine("DeepSeek Harness 菜单补丁安装器 v" + ToolVersion);
        Console.WriteLine("模式：" + (revert ? "还原补丁" : "安装补丁") + (dryRun ? "（演练，不写入）" : ""));
        Console.WriteLine();

        string asarPath = ResolveAsar(asarArg);
        FileInfo fi = new FileInfo(asarPath);
        Console.WriteLine("目标文件：" + asarPath);
        Console.WriteLine("文件大小：" + fi.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字节");
        Console.WriteLine();

        using (FileStream fs = new FileStream(asarPath, FileMode.Open, dryRun ? FileAccess.Read : FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            return Patch(fs, revert, backup, dryRun, force);
        }
    }

    private static int Patch(FileStream fs, bool revert, bool backup, bool dryRun, bool force)
    {
        // ---------- 1. 读 asar 头 ----------
        if (fs.Length < 16) throw new ToolException("app.asar 太小，不是有效的 asar 文件");
        byte[] head = ReadAt(fs, 0, 16);
        uint u0 = BitConverter.ToUInt32(head, 0);
        uint headerPickle = BitConverter.ToUInt32(head, 4);
        uint jsonLen = BitConverter.ToUInt32(head, 12);
        if (u0 != 4) throw new ToolException("asar 头部格式不认识（第一个字段应为 4，实际 " + u0 + "）");
        if (jsonLen == 0 || 16L + jsonLen > fs.Length) throw new ToolException("asar 头部长度异常（JSON 长度 " + jsonLen + "）");

        long dataStart = 16L + jsonLen;
        while (dataStart % 4 != 0) dataStart++;
        if (headerPickle != (uint)(dataStart - 8))
            throw new ToolException("asar 头部长度字段不一致：" + headerPickle + " != " + (dataStart - 8));

        byte[] jsonBytes = ReadAt(fs, 16, (int)jsonLen);
        string jsonText = Encoding.UTF8.GetString(jsonBytes);
        if (Encoding.UTF8.GetByteCount(jsonText) != jsonLen)
            throw new ToolException("asar 头部不是合法 UTF-8，已中止");

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;
        Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(jsonText);
        if (root == null) throw new ToolException("asar 头部 JSON 解析失败");

        // ---------- 2. 定位 lib/main.js ----------
        Dictionary<string, object> entry = GetEntry(root, "files", "lib", "files", "main.js");
        if (entry == null)
            throw new ToolException("asar 里没有 lib/main.js —— 这可能不是 DeepSeek Harness 的 app.asar，或版本结构已大改。");

        long offset = Long(entry, "offset");
        int size = (int)Long(entry, "size");
        if (offset < 0 || size <= 0 || (long)size + offset > fs.Length - dataStart)
            throw new ToolException("lib/main.js 的偏移/长度超出文件范围（offset=" + offset + ", size=" + size + "）");

        string pkgVersion = ReadPackageVersion(fs, root, dataStart);
        if (!string.IsNullOrEmpty(pkgVersion)) Console.WriteLine("应用版本：" + pkgVersion);
        Console.WriteLine("lib/main.js：offset=" + offset + ", size=" + size);

        // ---------- 3. 完整性记录 ----------
        Dictionary<string, object> integrity = Dict(entry, "integrity");
        string oldHash = null;
        long blockSize = 0;
        List<string> oldBlocks = new List<string>();
        if (integrity != null)
        {
            string algo = Str(integrity, "algorithm");
            if (!string.Equals(algo, "SHA256", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("不支持的完整性算法：" + algo);
            oldHash = Str(integrity, "hash");
            blockSize = Long(integrity, "blockSize");
            object blocksObj;
            integrity.TryGetValue("blocks", out blocksObj);
            IEnumerable blocksEnum = blocksObj as IEnumerable;
            if (blocksEnum != null)
            {
                foreach (object o in blocksEnum) oldBlocks.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            }
            Console.WriteLine("完整性记录：SHA256, blockSize=" + blockSize + ", blocks=" + oldBlocks.Count);
        }
        else
        {
            Console.WriteLine("完整性记录：无（跳过头哈希更新）");
        }

        // ---------- 4. 读 lib/main.js 内容并定位菜单模板 ----------
        byte[] content = ReadAt(fs, dataStart + offset, size);
        List<int> installHits = FindAll(content, InstallPrefix);
        List<int> patchedHits = FindAll(content, PatchedPrefix);

        bool alreadyDone = revert ? (patchedHits.Count == 0 && installHits.Count > 0)
                                  : (patchedHits.Count > 0 && installHits.Count == 0);
        if (alreadyDone)
        {
            Console.WriteLine();
            Console.WriteLine("[跳过] " + (revert ? "当前是未打补丁的原始版本，无需还原。" : "当前已打过补丁，无需重复安装。"));
            return 0;
        }

        if (revert)
        {
            if (installHits.Count > 0)
                throw new ToolException("lib/main.js 里仍能找到 development ? [（" + installHits.Count + " 处），说明当前没有安装补丁，无需 --revert。");
            if (patchedHits.Count != 1)
                throw new ToolException("找到 " + patchedHits.Count + " 处 true||false ? [，预期恰好 1 处，为避免误改已中止。");
        }
        else
        {
            if (patchedHits.Count > 0)
                throw new ToolException("lib/main.js 里已经有 true||false ? [（" + patchedHits.Count + " 处），可能已被其它方式修改；如确认重新安装请先 --revert。");
            if (installHits.Count != 1)
                throw new ToolException("找到 " + installHits.Count + " 处 development ? [，预期恰好 1 处。版本结构可能变化，已中止以免写坏文件。");
        }

        int hit = revert ? patchedHits[0] : installHits[0];

        // 上下文校验：确认命中的确实是那两个菜单项所在的模板
        int ctxFrom = Math.Max(0, hit - 1500);
        int ctxTo = Math.Min(content.Length, hit + 2000);
        string ctx = Encoding.UTF8.GetString(content, ctxFrom, ctxTo - ctxFrom);
        bool ctxOk = ctx.IndexOf("reloadPageMenu", StringComparison.Ordinal) >= 0
                  && ctx.IndexOf("restartAppHostMenu", StringComparison.Ordinal) >= 0;
        if (!ctxOk)
            throw new ToolException("特征位置附近没有找到 reloadPageMenu / restartAppHostMenu 标记，版本结构可能已变化，已中止以免误改。");
        Console.WriteLine("命中位置：lib/main.js +" + hit + "（上下文校验通过）");

        // ---------- 5. 修改前的一致性校验 ----------
        string calcHash = Sha256Hex(content, 0, content.Length);
        Console.WriteLine("当前内容 SHA256：" + calcHash);
        if (integrity != null)
        {
            if (!force && oldBlocks.Count != 1)
                throw new ToolException("该版本的 lib/main.js 完整性记录分成 " + oldBlocks.Count + " 块，本工具只支持单块情形，已中止（不会写坏文件）。");
            if (!force && !string.Equals(calcHash, oldHash, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("lib/main.js 内容哈希与 asar 头部记录不一致，文件可能被其它工具改过；确认无误请加 --force。");
            if (!force && oldBlocks.Count == 1 && !string.Equals(oldBlocks[0], oldHash, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("asar 头部完整性记录自相矛盾（hash 与 blocks[0] 不同），已中止。");
        }

        // ---------- 6. 在内存里打补丁 ----------
        byte[] token = Encoding.ASCII.GetBytes(revert ? DevToken : PatchToken);
        if (token.Length != DevToken.Length || token.Length != PatchToken.Length)
            throw new ToolException("内部错误：替换文本长度不是 11 字节");
        byte[] newContent = (byte[])content.Clone();
        Array.Copy(token, 0, newContent, hit, token.Length);
        string newHash = Sha256Hex(newContent, 0, newContent.Length);

        byte[] newJsonBytes = jsonBytes;
        string newJsonText = jsonText;
        if (integrity != null)
        {
            int occ = CountOccurrences(jsonText, oldHash);
            if (occ != 2)
                throw new ToolException("asar 头部里 " + oldHash + " 出现 " + occ + " 次（预期 2 次：hash 与 blocks[0]），已中止以免写坏文件。");
            newJsonText = jsonText.Replace(oldHash, newHash);
            newJsonBytes = Encoding.UTF8.GetBytes(newJsonText);
            if (newJsonBytes.Length != jsonBytes.Length)
                throw new ToolException("内部错误：头部 JSON 长度变化，已中止");
        }

        Console.WriteLine("将替换为：" + (revert ? DevToken : PatchToken) + "（绝对偏移 " + (dataStart + offset + hit) + "）");
        Console.WriteLine("新 SHA256：" + newHash);

        if (dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("[演练] 未写入任何字节。去掉 --dry-run 即真正执行。");
            return 0;
        }

        if (backup)
        {
            string bak = asarPathOf(fs) + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Copy(asarPathOf(fs), bak, true);
            Console.WriteLine("已备份： " + bak);
        }

        // ---------- 7. 写入（先内容，后头部哈希） ----------
        fs.Position = dataStart + offset;
        fs.Write(newContent, 0, newContent.Length);
        fs.Flush(true);

        if (integrity != null)
        {
            fs.Position = 16;
            fs.Write(newJsonBytes, 0, newJsonBytes.Length);
            fs.Flush(true);
        }

        // ---------- 8. 写后复核 ----------
        byte[] verifyContent = ReadAt(fs, dataStart + offset, size);
        string verifyHash = Sha256Hex(verifyContent, 0, verifyContent.Length);
        string verifyCtx = Encoding.UTF8.GetString(verifyContent, Math.Max(0, hit - 5), 40);
        bool contentOk = verifyHash == newHash && verifyCtx.IndexOf(revert ? InstallPrefix : PatchedPrefix, StringComparison.Ordinal) >= 0;

        bool headerOk = true;
        if (integrity != null)
        {
            byte[] verifyJson = ReadAt(fs, 16, (int)jsonLen);
            string verifyText = Encoding.UTF8.GetString(verifyJson);
            Dictionary<string, object> vroot = serializer.Deserialize<Dictionary<string, object>>(verifyText);
            Dictionary<string, object> ventry = GetEntry(vroot, "files", "lib", "files", "main.js");
            string vhash = ventry != null && Dict(ventry, "integrity") != null ? Str(Dict(ventry, "integrity"), "hash") : null;
            headerOk = string.Equals(vhash, newHash, StringComparison.OrdinalIgnoreCase) && verifyHash == newHash;
        }

        Console.WriteLine();
        if (contentOk && headerOk)
        {
            Console.WriteLine("[成功] " + (revert ? "已还原为原始 development 条件。" : "补丁已写入：...true||false ? ["));
            Console.WriteLine("       头部完整性哈希已同步为 " + newHash + "，asar 偏移未变。");
        }
        else
        {
            WriteError("写入后的复核未通过（contentOk=" + contentOk + ", headerOk=" + headerOk + "），请从备份或安装包恢复。");
            return 1;
        }

        bool running = IsAppRunning();
        Console.WriteLine();
        if (revert)
        {
            Console.WriteLine("提示：如需让还原生效，请退出并重新打开 DeepSeek Harness。");
        }
        else if (running)
        {
            Console.WriteLine("提示：DeepSeek Harness 正在运行，当前进程仍是旧代码。");
            Console.WriteLine("      请完全退出（含托盘图标）后重新打开，菜单里就会出现“刷新页面 / 重启应用与 Host”。");
        }
        else
        {
            Console.WriteLine("提示：下次启动 DeepSeek Harness 后，菜单里会出现“刷新页面 / 重启应用与 Host”。");
        }
        return 0;
    }

    // ================= 工具函数 =================

    private static string asarPathOf(FileStream fs) { return fs.Name; }

    private static void PrintHelp()
    {
        Console.WriteLine("DeepSeek Harness 菜单补丁安装器 v" + ToolVersion);
        Console.WriteLine();
        Console.WriteLine("解锁被 development 条件隐藏的两个菜单项：刷新页面 / 重启应用与 Host。");
        Console.WriteLine();
        Console.WriteLine("用法：DeepSeekHarnessMenuPatch.exe [选项] [app.asar 路径]");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --asar <路径>  指定 app.asar（默认自动查找 %LOCALAPPDATA%\\Programs\\DeepSeek Harness\\resources\\app.asar）");
        Console.WriteLine("  --revert, -r   还原补丁（development <-> true||false 反向替换）");
        Console.WriteLine("  --dry-run, -d  只检查、不写入");
        Console.WriteLine("  --backup, -b   写入前把 app.asar 复制一份 .bak-时间戳 备份（约 180MB）");
        Console.WriteLine("  --force, -f    跳过哈希一致性校验（不推荐）");
        Console.WriteLine("  --no-pause, -n 结束后不等待回车");
        Console.WriteLine("  --help, -h     显示本帮助");
        Console.WriteLine();
        Console.WriteLine("说明：桌面版自动更新会覆盖 app.asar，升级后重新运行本程序即可。程序可重复运行，已打过补丁时会直接提示跳过。");
    }

    private static bool HasArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void WriteError(string message)
    {
        Console.WriteLine();
        Console.WriteLine("[错误] " + message);
    }

    private static string ResolveAsar(string explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            string p = explicitPath;
            if (p.StartsWith("\"") && p.EndsWith("\"") && p.Length > 2) p = p.Substring(1, p.Length - 2);
            try { p = Path.GetFullPath(p); } catch { }
            if (Directory.Exists(p)) p = Path.Combine(p, "resources", "app.asar");
            if (!File.Exists(p)) throw new ToolException("指定的 app.asar 不存在：" + p);
            return p;
        }

        List<string> candidates = new List<string>();
        string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(lad)) candidates.Add(Path.Combine(lad, "Programs", "DeepSeek Harness", "resources", "app.asar"));

        string[] pfVars = new string[] { "ProgramFiles", "ProgramFiles(x86)" };
        for (int i = 0; i < pfVars.Length; i++)
        {
            string pf = Environment.GetEnvironmentVariable(pfVars[i]);
            if (string.IsNullOrEmpty(pf)) continue;
            candidates.Add(Path.Combine(pf, "DeepSeek Harness", "resources", "app.asar"));
        }

        try
        {
            Process[] procs = Process.GetProcessesByName("DeepSeek Harness");
            for (int i = 0; i < procs.Length; i++)
            {
                try
                {
                    string exe = procs[i].MainModule.FileName;
                    if (!string.IsNullOrEmpty(exe))
                        candidates.Add(Path.Combine(Path.GetDirectoryName(exe), "resources", "app.asar"));
                }
                catch { }
                finally { procs[i].Dispose(); }
            }
        }
        catch { }

        for (int i = 0; i < candidates.Count; i++)
        {
            if (File.Exists(candidates[i])) return candidates[i];
        }
        throw new ToolException("找不到 app.asar。请用 --asar 指定路径，例如：\n       DeepSeekHarnessMenuPatch.exe --asar \"C:\\Users\\你\\AppData\\Local\\Programs\\DeepSeek Harness\\resources\\app.asar\"");
    }

    private static byte[] ReadAt(Stream s, long position, int count)
    {
        byte[] buf = new byte[count];
        s.Position = position;
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) throw new ToolException("读取 app.asar 时遇到意外的文件结尾");
            read += n;
        }
        return buf;
    }

    private static string Sha256Hex(byte[] data, int offset, int count)
    {
        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(data, offset, count);
            StringBuilder sb = new StringBuilder(64);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    private static int IndexOfBytes(byte[] hay, byte[] needle, int start)
    {
        int last = hay.Length - needle.Length;
        for (int i = start; i <= last; i++)
        {
            if (hay[i] != needle[0]) continue;
            bool ok = true;
            for (int k = 1; k < needle.Length; k++)
            {
                if (hay[i + k] != needle[k]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static List<int> FindAll(byte[] hay, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        List<int> hits = new List<int>();
        int i = 0;
        while (true)
        {
            int p = IndexOfBytes(hay, needle, i);
            if (p < 0) break;
            hits.Add(p);
            i = p + 1;
        }
        return hits;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while (true)
        {
            int p = haystack.IndexOf(needle, i, StringComparison.Ordinal);
            if (p < 0) break;
            count++;
            i = p + 1;
        }
        return count;
    }

    private static Dictionary<string, object> GetEntry(Dictionary<string, object> root, params string[] keys)
    {
        object cur = root;
        for (int i = 0; i < keys.Length; i++)
        {
            Dictionary<string, object> d = cur as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(keys[i], out v)) return null;
            cur = v;
        }
        return cur as Dictionary<string, object>;
    }

    private static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
    {
        if (d == null) return null;
        object v;
        if (!d.TryGetValue(key, out v)) return null;
        return v as Dictionary<string, object>;
    }

    private static string Str(Dictionary<string, object> d, string key)
    {
        if (d == null) return null;
        object v;
        if (!d.TryGetValue(key, out v) || v == null) return null;
        string s = v as string;
        if (s != null) return s;
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    private static long Long(Dictionary<string, object> d, string key)
    {
        string s = Str(d, key);
        if (s == null) throw new ToolException("asar 头部缺少字段：" + key);
        long v;
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            throw new ToolException("asar 头部字段 " + key + " 不是整数：" + s);
        return v;
    }

    private static string ReadPackageVersion(FileStream fs, Dictionary<string, object> root, long dataStart)
    {
        try
        {
            Dictionary<string, object> entry = GetEntry(root, "files", "package.json");
            if (entry == null) return null;
            long offset = Long(entry, "offset");
            int size = (int)Long(entry, "size");
            if (size <= 0 || size > 1024 * 1024) return null;
            byte[] buf = ReadAt(fs, dataStart + offset, size);
            string text = Encoding.UTF8.GetString(buf);
            Match m = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAppRunning()
    {
        try
        {
            Process[] procs = Process.GetProcessesByName("DeepSeek Harness");
            bool any = procs.Length > 0;
            for (int i = 0; i < procs.Length; i++) procs[i].Dispose();
            return any;
        }
        catch
        {
            return false;
        }
    }
}
