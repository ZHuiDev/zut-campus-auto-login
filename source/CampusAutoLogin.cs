using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace CampusAutoLogin
{
    public class Settings
    {
        public string Account = "";
        public string Secret = "";
        public string Carrier = "联通";
        public bool AutoStart;
        public bool Reconnect;
        public bool Schedule;
        public bool ConnectWifi = true;
        public int IntervalSeconds = 60;
        public string DailyTime = "08:00";
        public string Password()
        {
            if (String.IsNullOrEmpty(Secret)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Secret), null, DataProtectionScope.CurrentUser));
        }
        public void SetPassword(string value)
        {
            Secret = String.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }

    public static class Storage
    {
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZYUTCampusLogin");
        public static readonly string ConfigPath = Path.Combine(Folder, "settings.xml");
        public static Settings Load()
        {
            if (!File.Exists(ConfigPath)) return new Settings();
            using (var f = File.OpenRead(ConfigPath)) return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(f);
        }
        public static void Save(Settings s)
        {
            Directory.CreateDirectory(Folder);
            string temp = ConfigPath + ".new";
            using (var f = File.Create(temp)) new XmlSerializer(typeof(Settings)).Serialize(f, s);
            if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, null); else File.Move(temp, ConfigPath);
        }
        public static void SetStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (!enabled) { key.DeleteValue("ZYUTCampusLogin", false); return; }
                Directory.CreateDirectory(Folder);
                string installed = Path.Combine(Folder, "CampusAutoLogin.exe");
                string current = Application.ExecutablePath;
                if (!String.Equals(installed, current, StringComparison.OrdinalIgnoreCase)) File.Copy(current, installed, true);
                key.SetValue("ZYUTCampusLogin", "\"" + installed + "\" --background");
            }
        }
    }

    public static class Net
    {
        public const string Ssid = "zut-stu";
        public const string Portal = "http://1.1.1.1/";
        public static async Task<string> Command(string arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using (var p = new Process())
            {
                p.StartInfo = new ProcessStartInfo("netsh.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                p.Start();
                Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                Task<string> stderr = p.StandardError.ReadToEndAsync();
                Task exit = Task.Run(() => p.WaitForExit());
                if (await Task.WhenAny(exit, Task.Delay(8000, ct)) != exit)
                {
                    try { p.Kill(); } catch { }
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Windows Wi-Fi 命令超时。");
                }
                await stderr;
                return await stdout;
            }
        }
        public static string ParseSsid(string output)
        {
            var m = Regex.Match(output, @"^\s*SSID\s*[:：]\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        public static async Task<bool> EnsureWifi(bool manual, bool connectWifi, Action<string> log, CancellationToken ct)
        {
            string ssid = ParseSsid(await Command("wlan show interfaces", ct));
            if (ssid == Ssid) return true;
            if (!manual && !String.IsNullOrEmpty(ssid)) { log("当前 Wi-Fi 不是 zut-stu，后台登录已跳过。"); return false; }
            if (!connectWifi) { log("请先连接 zut-stu 校园 Wi-Fi。"); return false; }
            log("正在连接 Windows 已保存的 zut-stu Wi-Fi…");
            await Command("wlan connect name=\"zut-stu\" ssid=\"zut-stu\"", ct);
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(1500, ct);
                if (ParseSsid(await Command("wlan show interfaces", ct)) == Ssid) return true;
            }
            log("未连接到 zut-stu。请先用 Windows 连接一次并保存该 Wi-Fi，再重试。");
            return false;
        }
        public static async Task<bool> Probe(string url, string expected, CancellationToken ct)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                timeout.CancelAfter(5000);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null;
                req.AllowAutoRedirect = false;
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                req.UserAgent = "ZYUTCampusLogin/1.0";
                using (timeout.Token.Register(() => req.Abort()))
                using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    var buf = new char[4096];
                    string body = new string(buf, 0, await sr.ReadAsync(buf, 0, buf.Length));
                    return body.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                }
            }
            catch { ct.ThrowIfCancellationRequested(); return false; }
        }
        public static async Task<bool> Online(CancellationToken ct)
        {
            bool[] result = await Task.WhenAll(
                Probe("https://www.baidu.com/robots.txt", "User-agent: Baiduspider", ct),
                Probe("http://www.msftconnecttest.com/connecttest.txt", "Microsoft Connect Test", ct));
            return result.Any(x => x);
        }
    }

    // Own isolated headless Edge/Chrome process. Never attaches to a personal browser.
    public interface IPortalBrowser : IDisposable
    {
        Task Start(CancellationToken ct);
        Task Navigate(string url, CancellationToken ct);
        Task<string> Eval(string code, CancellationToken ct);
    }

    public sealed class Browser : IPortalBrowser
    {
        private Process process;
        private ClientWebSocket socket;
        private string profile;
        private int sequence;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public static string FindBrowser()
        {
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            foreach (string product in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" })
                foreach (string root in roots)
                {
                    string path = Path.Combine(root, product);
                    if (File.Exists(path)) return path;
                }
            throw new InvalidOperationException("未找到 Microsoft Edge 或 Chrome，请安装其中一个浏览器。");
        }
        private static async Task<string> ReadLocal(string url, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
            timeout.CancelAfter(3000);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null; req.Timeout = 3000;
            using (timeout.Token.Register(() => req.Abort()))
            using (var resp = await req.GetResponseAsync())
            using (var sr = new StreamReader(resp.GetResponseStream())) return await sr.ReadToEndAsync();
            }
        }
        public async Task Start(CancellationToken ct)
        {
            profile = Path.Combine(Path.GetTempPath(), "ZYUTCampus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            process = Process.Start(new ProcessStartInfo(FindBrowser(), "--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-background-networking --disable-extensions --disable-sync --no-proxy-server --remote-debugging-address=127.0.0.1 --remote-debugging-port=0 --user-data-dir=\"" + profile + "\" about:blank") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            string portFile = Path.Combine(profile, "DevToolsActivePort");
            for (int i = 0; i < 80 && !File.Exists(portFile); i++) { ct.ThrowIfCancellationRequested(); await Task.Delay(150, ct); }
            if (!File.Exists(portFile)) throw new InvalidOperationException("后台浏览器启动失败，请检查 Edge / Chrome 是否可以正常运行。");
            string port = File.ReadAllLines(portFile)[0];
            int portNum;
            if (!Int32.TryParse(port, out portNum) || portNum < 1 || portNum > 65535) throw new IOException("后台浏览器端口无效。");
            var pages = json.DeserializeObject(await ReadLocal("http://127.0.0.1:" + port + "/json/list", ct)) as object[];
            string ws = null;
            foreach (object item in pages)
            {
                var page = item as Dictionary<string, object>;
                if (page != null && Convert.ToString(page["type"]) == "page") { ws = Convert.ToString(page["webSocketDebuggerUrl"]); break; }
            }
            if (ws == null) throw new IOException("无法建立后台浏览器连接。");
            var target = new Uri(ws);
            if (!target.IsLoopback || target.Port != portNum) throw new IOException("后台浏览器连接地址无效。");
            socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            await socket.ConnectAsync(target, ct);
            await Call("Page.enable", new { }, ct);
            await Call("Page.addScriptToEvaluateOnNewDocument", new { source = "window.__campusMessage='';window.alert=function(v){window.__campusMessage=String(v)};" }, ct);
        }
        public async Task<Dictionary<string, object>> Call(string method, object parameters, CancellationToken ct)
        {
            int id = ++sequence;
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(new { id = id, method = method, @params = parameters }));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            byte[] buffer = new byte[32768];
            while (true)
            {
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (r.MessageType == WebSocketMessageType.Close) throw new IOException("后台浏览器连接已关闭。");
                        ms.Write(buffer, 0, r.Count);
                        if (ms.Length > 4 * 1024 * 1024) throw new IOException("后台浏览器响应过大。");
                    } while (!r.EndOfMessage);
                    var value = json.DeserializeObject(Encoding.UTF8.GetString(ms.ToArray())) as Dictionary<string, object>;
                    if (value != null && value.ContainsKey("id") && Convert.ToInt32(value["id"]) == id)
                    {
                        if (value.ContainsKey("error")) throw new IOException("后台浏览器命令执行失败。");
                        return value;
                    }
                }
            }
        }
        public async Task<string> Eval(string code, CancellationToken ct)
        {
            var response = await Call("Runtime.evaluate", new { expression = code, returnByValue = true }, ct);
            var outer = (Dictionary<string, object>)response["result"];
            if (outer.ContainsKey("exceptionDetails")) throw new InvalidOperationException("登录页面脚本无法执行。");
            var result = (Dictionary<string, object>)outer["result"];
            return result.ContainsKey("value") ? Convert.ToString(result["value"], CultureInfo.InvariantCulture) : "";
        }
        public async Task Navigate(string url, CancellationToken ct)
        {
            await Call("Page.navigate", new { url = url }, ct);
            await Task.Delay(1200, ct);
        }
        public void Dispose()
        {
            if (socket != null) { try { socket.Abort(); socket.Dispose(); } catch { } }
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        using (var killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) killer.WaitForExit(4000);
                    }
                }
                catch { }
                process.Dispose();
            }
            // Only delete the unique temporary profile created by this instance.
            if (profile != null && Path.GetFileName(profile).StartsWith("ZYUTCampus-", StringComparison.Ordinal) && Path.GetDirectoryName(profile).TrimEnd('\\').Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(profile, true); } catch { }
        }
    }

    public static class PortalScripts
    {
        public static string Fill(string account, string password, string carrier, bool test)
        {
            var js = new JavaScriptSerializer();
            return "(function(){const cfg=" + js.Serialize(new { account = account, password = password, carrier = carrier, test = test }) + ";" + @"
const visible=e=>!!e&&e.getClientRects().length>0&&!e.disabled;
const text=(document.body?document.body.innerText:'');
const knownPage=/\/a70\.htm$/i.test(location.pathname)||!!document.querySelector('input[name=DDDDD],input[name=upass],input[name=user_account]')||text.includes('中原工学院');
if(!cfg.test && (!['1.1.1.1','10.10.9.200'].includes(location.hostname) || !knownPage))return 'WRONG_PAGE';
const pw=Array.from(document.querySelectorAll('input[type=password]')).find(visible);
const users=Array.from(document.querySelectorAll('input')).filter(e=>visible(e)&&['text','tel','number','email',''].includes((e.type||'').toLowerCase()));
const user=users.find(e=>/DDDDD|username|user_account|userid|account|学号/i.test(e.name+' '+e.id+' '+e.placeholder))||users[0];
if(!pw||!user)return /退出|注销|logout|已登录/i.test(text)?'SESSION':'WAIT';
const captcha=Array.from(document.querySelectorAll('input')).some(e=>visible(e)&&/验证码|captcha|checkcode|verifycode/i.test(e.name+' '+e.id+' '+e.placeholder));
if(captcha)return 'CAPTCHA';
const selects=Array.from(document.querySelectorAll('select')).filter(visible);
let select=null,option=null;
for(const s of selects){const opt=Array.from(s.options).find(o=>o.text.includes(cfg.carrier));if(opt){select=s;option=opt;break}}
if(!select||!option)return 'CARRIER';
const set=(e,v)=>{const p=e.tagName==='SELECT'?HTMLSelectElement.prototype:HTMLInputElement.prototype;Object.getOwnPropertyDescriptor(p,'value').set.call(e,v);e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));e.dispatchEvent(new Event('blur',{bubbles:true}))};
const login=Array.from(document.querySelectorAll('button,input[type=button],input[type=submit],a')).find(e=>visible(e)&&/^(登录|登陆|login|log in|上网登录|连接)$/i.test((e.innerText||e.value||'').replace(/\s/g,''))) || Array.from(document.querySelectorAll('[onclick]')).find(e=>visible(e)&&/^(登录|登陆)$/.test(e.innerText.replace(/\s/g,'')));
if(!login)return 'BUTTON';
set(user,cfg.account);set(pw,cfg.password);set(select,option.value);
window.__campusMessage='';login.click();return 'SUBMITTED';})()";
        }
        public const string Error = @"(function(){const t=(window.__campusMessage||'')+' '+(document.body?document.body.innerText:'');if(/欠费|余额不足/.test(t))return 'BALANCE';if(/密码.{0,8}(错误|不正确)|账号或密码|用户名.{0,6}(错误|不存在)|账户.{0,6}(锁定|停用)|账号.{0,6}(锁定|停用)|password.{0,8}(wrong|incorrect|invalid)|authentication failed|认证失败|认证被拒绝|登录失败|登陆失败|错误码\s*[:：]?\s*[1-9]/i.test(t))return 'AUTH';if(/验证码/.test(window.__campusMessage||''))return 'CAPTCHA';return '';})()";
    }

    public enum LoginResult { Online, Skip, Failed, NeedsAttention }
    public sealed class DailySchedule
    {
        public DateTime Next { get; private set; }
        public DailySchedule(string time, DateTime now)
        {
            TimeSpan parsed;
            if (!TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out parsed)) parsed = TimeSpan.FromHours(8);
            Next = now.Date.Add(parsed);
            if (Next <= now) Next = Next.AddDays(1);
        }
        public bool TakeDue(DateTime now)
        {
            if (now < Next) return false;
            do { Next = Next.AddDays(1); } while (Next <= now);
            return true;
        }
    }
    public sealed class LoginEngine
    {
        private readonly Func<IPortalBrowser> makeBrowser;
        private readonly Func<CancellationToken, Task<bool>> online;
        private readonly Func<bool, bool, Action<string>, CancellationToken, Task<bool>> ensureWifi;
        private readonly Func<int, CancellationToken, Task> delay;
        public LoginEngine() : this(() => new Browser(), Net.Online, Net.EnsureWifi, (ms, ct) => Task.Delay(ms, ct)) { }
        internal LoginEngine(Func<IPortalBrowser> browser, Func<CancellationToken, Task<bool>> check, Func<bool, bool, Action<string>, CancellationToken, Task<bool>> wifi, Func<int, CancellationToken, Task> wait)
        { makeBrowser = browser; online = check; ensureWifi = wifi; delay = wait; }
        public async Task<LoginResult> Run(Settings cfg, bool manual, Action<string> log, CancellationToken ct)
        {
            if (String.IsNullOrWhiteSpace(cfg.Account) || String.IsNullOrEmpty(cfg.Password())) { log("请先填写账号和密码，然后保存设置。"); return LoginResult.NeedsAttention; }
            if (!await ensureWifi(manual, cfg.ConnectWifi, log, ct)) return LoginResult.Skip;
            if (await online(ct)) { log("当前已连接互联网，无需重复登录。"); return LoginResult.Online; }
            log("正在打开中原工学院登录页…");
            using (var browser = makeBrowser())
            {
                await browser.Start(ct);
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await browser.Navigate(Net.Portal, ct);
                    string state = "WAIT";
                    for (int i = 0; i < 18; i++)
                    {
                        try { state = await browser.Eval(PortalScripts.Fill(cfg.Account, cfg.Password(), cfg.Carrier, false), ct); }
                        catch (InvalidOperationException) { state = "WAIT"; }
                        if (state != "WAIT" && state != "WRONG_PAGE") break;
                        await delay(600, ct);
                    }
                    if (state == "WAIT" || state == "WRONG_PAGE")
                    {
                        // Some Dr.COM gateways expose the page directly without a root redirect.
                        await browser.Navigate("http://1.1.1.1/a70.htm", ct);
                        for (int i = 0; i < 10; i++)
                        {
                            state = await browser.Eval(PortalScripts.Fill(cfg.Account, cfg.Password(), cfg.Carrier, false), ct);
                            if (state != "WAIT" && state != "WRONG_PAGE") break;
                            await delay(600, ct);
                        }
                    }
                    if (state == "SUBMITTED") log("已提交第 " + attempt + "/2 次登录，正在等待联网…");
                    else if (state == "SESSION") log("登录页显示已有会话，正在检测联网…");
                    else
                    {
                        log(Explain(state));
                        return state == "CAPTCHA" || state == "CARRIER" || state == "BUTTON" ? LoginResult.NeedsAttention : LoginResult.Failed;
                    }
                    for (int wait = 0; wait < 3; wait++)
                    {
                        await delay(1800, ct);
                        string err = "";
                        try { err = await browser.Eval(PortalScripts.Error, ct); } catch (InvalidOperationException) { }
                        if (err != "") { log(Explain(err)); return LoginResult.NeedsAttention; }
                        if (await online(ct)) { log("连接成功，互联网检测已通过。"); return LoginResult.Online; }
                    }
                    if (attempt == 1) log("第一次后仍未联网，准备按你的使用习惯进行第二次登录…");
                }
            }
            log("两次尝试后仍未检测到互联网。请打开网页登录页查看提示；自动重连不会连续密集提交。");
            return LoginResult.Failed;
        }
        public static string Explain(string state)
        {
            switch (state)
            {
                case "CAPTCHA": return "登录页要求验证码，请先使用浏览器手动登录。本轮自动尝试已停止。";
                case "CARRIER": return "登录页中未找到所选运营商，请打开登录页核对选项。";
                case "BUTTON": return "未识别到登录按钮，需要根据实际登录页调整程序。";
                case "BALANCE": return "登录页提示欠费或余额不足，已暂停自动尝试。";
                case "AUTH": return "登录页提示认证失败，请核对账号、密码和联通选项；已暂停自动尝试。";
                default: return "未识别到中原工学院登录页，未填写账号密码。请确认已连接 zut-stu，并打开登录页检查。";
            }
        }
    }

    public class MainForm : Form
    {
        private Settings cfg;
        private TextBox account, password, logs;
        private ComboBox carrier;
        private CheckBox startup, reconnect, schedule, wifi;
        private NumericUpDown interval;
        private DateTimePicker daily;
        private Button connect, save, cancel;
        private Label status;
        private NotifyIcon tray;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer activateTimer = new System.Windows.Forms.Timer();
        private CancellationTokenSource cancellation;
        private DateTime nextCheck;
        private DailySchedule dailySchedule;
        private bool busy, exiting, paused;
        private int failures;
        private string lastLog = "";
        private readonly bool background;
        public MainForm(bool bg) : this(bg, false) { }
        internal MainForm(bool bg, bool preview)
        {
            background = bg;
            Text = "校园网自动连接 · 中原工学院";
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(810, 678);
            MinimumSize = new Size(826, 717);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(244, 247, 250);
            BuildUi();
            try { cfg = preview ? new Settings() : Storage.Load(); cfg.Password(); }
            catch { cfg = new Settings(); Log("保存的设置无法读取，请重新输入并保存。原文件未被覆盖。"); }
            account.Text = cfg.Account; password.Text = cfg.Password(); carrier.Text = cfg.Carrier;
            startup.Checked = cfg.AutoStart; reconnect.Checked = cfg.Reconnect; schedule.Checked = cfg.Schedule; wifi.Checked = cfg.ConnectWifi;
            interval.Value = Math.Max(30, Math.Min(1800, cfg.IntervalSeconds));
            DateTime parsed;
            if (DateTime.TryParseExact(cfg.DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) daily.Value = parsed;
            ResetSchedule();
            tray = new NotifyIcon { Text = "中原工学院校园网自动连接", Icon = SystemIcons.Application, Visible = !preview };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => Reveal());
            menu.Items.Add("立即连接", null, async (s, e) => { Reveal(); await Connect(true); });
            menu.Items.Add("退出程序", null, (s, e) => Exit());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => Reveal();
            timer.Interval = 5000;
            timer.Tick += async (s, e) => await Tick();
            if (!preview) timer.Start();
            activateTimer.Interval = 500;
            activateTimer.Tick += (s, e) => { if (Program.Activate.WaitOne(0)) Reveal(); };
            if (!preview) activateTimer.Start();
            Shown += async (s, e) =>
            {
                if (preview) return;
                if (background) Hide();
                if (!String.IsNullOrWhiteSpace(cfg.Account) && cfg.Secret != "") await Connect(!background);
                else Log("首次使用：填写学号和密码，运营商已选联通，点击“保存并连接”。");
            };
            FormClosing += (s, e) =>
            {
                if (!exiting && (cfg.Reconnect || cfg.Schedule))
                {
                    e.Cancel = true; Hide();
                    tray.ShowBalloonTip(2500, "校园网工具正在后台运行", "双击托盘图标可打开；右键选择“退出程序”可完全停止。", ToolTipIcon.Info);
                }
                else
                {
                    exiting = true;
                    if (busy && cancellation != null) { e.Cancel = true; cancellation.Cancel(); Hide(); }
                    else tray.Visible = false;
                }
            };
            FormClosed += (s, e) => { timer.Dispose(); activateTimer.Dispose(); tray.Dispose(); };
        }
        private Label LabelAt(string text, int x, int y, int width)
        {
            var l = new Label { Text = text, Location = new Point(x, y), Size = new Size(width, 28), BackColor = Color.Transparent };
            Controls.Add(l); return l;
        }
        private Button ButtonAt(string text, int x, int y, int width, bool primary)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 40), FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(20, 114, 99) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(40, 55, 65), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = primary ? b.BackColor : Color.FromArgb(205, 216, 224);
            Controls.Add(b); return b;
        }
        private void BuildUi()
        {
            var heading = LabelAt("校园网，一键连接", 30, 22, 600);
            heading.Font = new Font(Font.FontFamily, 22, FontStyle.Bold); heading.Height = 48;
            LabelAt("中原工学院  /  zut-stu  /  联通    ·    未联网时最多登录两次", 32, 78, 750).ForeColor = Color.FromArgb(94, 112, 125);
            LabelAt("学号 / 账号", 32, 128, 110);
            account = new TextBox { Location = new Point(150, 124), Width = 246 }; Controls.Add(account);
            LabelAt("密码", 420, 128, 65);
            password = new TextBox { Location = new Point(485, 124), Width = 290, UseSystemPasswordChar = true }; Controls.Add(password);
            LabelAt("运营商", 32, 177, 110);
            carrier = new ComboBox { Location = new Point(150, 173), Width = 246, DropDownStyle = ComboBoxStyle.DropDownList };
            carrier.Items.AddRange(new object[] { "联通", "移动", "电信", "校园网" }); carrier.SelectedIndex = 0; Controls.Add(carrier);
            wifi = new CheckBox { Text = "连接已保存的 zut-stu Wi-Fi", Location = new Point(420, 174), Width = 355, Height = 30 }; Controls.Add(wifi);
            startup = new CheckBox { Text = "登录 Windows 后自动运行并登录校园网", Location = new Point(32, 226), Width = 700, Height = 30 }; Controls.Add(startup);
            reconnect = new CheckBox { Text = "断线自动重连", Location = new Point(32, 270), Width = 190, Height = 30 }; Controls.Add(reconnect);
            LabelAt("检查间隔", 245, 273, 90);
            interval = new NumericUpDown { Location = new Point(340, 269), Width = 90, Minimum = 30, Maximum = 1800, Increment = 30, Value = 60 }; Controls.Add(interval);
            LabelAt("秒（连续失败时自动延长）", 445, 273, 325);
            schedule = new CheckBox { Text = "每天定时登录", Location = new Point(32, 314), Width = 190, Height = 30 }; Controls.Add(schedule);
            daily = new DateTimePicker { Location = new Point(245, 313), Width = 120, Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = DateTime.Today.AddHours(8) }; Controls.Add(daily);
            LabelAt("程序需保持运行；休眠后唤醒时补检", 390, 317, 395).ForeColor = Color.FromArgb(94, 112, 125);
            save = ButtonAt("保存并连接", 32, 368, 170, true);
            save.Click += async (s, e) => { if (Save()) await Connect(true); };
            connect = ButtonAt("仅保存设置", 218, 368, 130, false);
            connect.Click += (s, e) => Save();
            cancel = ButtonAt("停止本次", 364, 368, 130, false); cancel.Enabled = false;
            cancel.Click += (s, e) => { if (cancellation != null) cancellation.Cancel(); paused = true; Log("已暂停后台自动尝试；点击连接或保存设置可恢复。"); };
            var open = ButtonAt("打开登录页", 510, 368, 130, false);
            open.Click += (s, e) => { try { Process.Start(new ProcessStartInfo("http://1.1.1.1/a70.htm") { UseShellExecute = true }); } catch { Log("无法打开浏览器，请手动访问 http://1.1.1.1/a70.htm"); } };
            var exit = ButtonAt("退出", 656, 368, 120, false); exit.Click += (s, e) => Exit();
            status = LabelAt("等待设置", 32, 429, 745); status.Font = new Font(Font, FontStyle.Bold);
            logs = new TextBox { Location = new Point(32, 467), Size = new Size(744, 150), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right }; Controls.Add(logs);
            var note = LabelAt("密码使用 Windows 账户加密保存。关闭窗口可留在托盘，点“退出”完全停止。", 32, 636, 745);
            note.Font = new Font(Font.FontFamily, 9); note.ForeColor = Color.FromArgb(94, 112, 125); note.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        }
        private bool Save()
        {
            if (String.IsNullOrWhiteSpace(account.Text) || password.Text.Length == 0) { Log("请先填写学号和密码。"); return false; }
            try
            {
                var next = new Settings { Account = account.Text.Trim(), Carrier = carrier.Text, AutoStart = startup.Checked, Reconnect = reconnect.Checked, Schedule = schedule.Checked, ConnectWifi = wifi.Checked, IntervalSeconds = (int)interval.Value, DailyTime = daily.Value.ToString("HH:mm", CultureInfo.InvariantCulture) };
                next.SetPassword(password.Text);
                Storage.SetStartup(next.AutoStart);
                Storage.Save(next);
                cfg = next; paused = false; failures = 0; ResetSchedule();
                Log("设置已保存。" + (cfg.AutoStart ? "已启用 Windows 登录后自动启动。" : ""));
                return true;
            }
            catch { Log("设置保存失败，请确认文件可写，并检查开机启动是否被系统策略限制。"); return false; }
        }
        private void ResetSchedule()
        {
            nextCheck = DateTime.Now.AddSeconds(Math.Max(30, cfg.IntervalSeconds));
            dailySchedule = new DailySchedule(cfg.DailyTime, DateTime.Now);
        }
        private async Task Tick()
        {
            if (busy || paused || exiting) return;
            DateTime now = DateTime.Now;
            if (cfg.Schedule && dailySchedule.TakeDue(now))
            {
                Log("到达定时登录时间。"); await Connect(false); return;
            }
            if (cfg.Reconnect && now >= nextCheck) await Connect(false);
        }
        private async Task Connect(bool manual)
        {
            if (busy || exiting) return;
            busy = true; if (manual) { paused = false; failures = 0; }
            connect.Enabled = save.Enabled = false; cancel.Enabled = true;
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            try
            {
                LoginResult result = await new LoginEngine().Run(cfg, manual, Log, cancellation.Token);
                if (result == LoginResult.Online) { failures = 0; status.ForeColor = Color.FromArgb(20, 114, 99); }
                else if (result == LoginResult.NeedsAttention) paused = true;
                else if (result == LoginResult.Failed)
                {
                    failures++;
                    if (failures >= 3) { paused = true; Log("连续三轮未连接成功，已暂停自动尝试。检查网页登录后点击连接恢复。"); }
                }
            }
            catch (OperationCanceledException)
            {
                Log("本次连接已停止或超时。");
                if (!paused && !exiting)
                {
                    failures++;
                    if (failures >= 3) { paused = true; Log("连续三轮连接超时，已暂停自动尝试；点击连接可恢复。"); }
                }
            }
            catch (Exception ex)
            {
                failures++;
                // Do not log browser responses, URLs with credentials or exception dumps.
                Log(ex is InvalidOperationException ? ex.Message : "连接过程中发生错误，请确认 Wi-Fi 和 Edge / Chrome 可正常使用。");
                if (failures >= 3) { paused = true; Log("连续三轮错误，已暂停自动尝试；点击连接可恢复。"); }
            }
            finally
            {
                int delay = Math.Max(30, Math.Min(1800, cfg.IntervalSeconds));
                delay = Math.Min(1800, delay * (1 << Math.Min(failures, 4)));
                nextCheck = DateTime.Now.AddSeconds(delay);
                busy = false;
                if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
                if (!IsDisposed) { connect.Enabled = save.Enabled = true; cancel.Enabled = false; }
                if (exiting && !IsDisposed) Close();
            }
        }
        private void Log(string message)
        {
            if (IsDisposed) return;
            status.Text = message; status.ForeColor = Color.FromArgb(50, 66, 80);
            if (lastLog == message) return;
            lastLog = message;
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            if (logs.TextLength > 24000) logs.Text = logs.Text.Substring(logs.TextLength - 12000);
            logs.AppendText(line + Environment.NewLine);
            try
            {
                Directory.CreateDirectory(Storage.Folder);
                string file = Path.Combine(Storage.Folder, "activity.log");
                if (File.Exists(file) && new FileInfo(file).Length > 262144) File.WriteAllText(file, "");
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        private void Reveal() { if (exiting) return; Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void Exit()
        {
            exiting = true; timer.Stop();
            if (cancellation != null) { cancellation.Cancel(); Hide(); }
            else Close();
        }
    }

    public static class Program
    {
        public static EventWaitHandle Activate;
        [STAThread]
        public static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            bool created;
            using (var mutex = new Mutex(true, @"Local\ZYUTCampusLogin-" + Environment.UserName, out created))
            using (Activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ZYUTCampusLogin-Show-" + Environment.UserName))
            {
                if (!created) { Activate.Set(); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(args.Contains("--background")));
            }
        }
    }
}
