using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Serialization;
using CampusAutoLogin;

public static class CampusTests
{
    private static readonly List<string> passed = new List<string>();
    private static void Assert(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        passed.Add("PASS: " + name);
    }
    private sealed class FakeBrowser : IPortalBrowser
    {
        public int Submissions, Navigations;
        public bool Disposed;
        public string Error = "", State = "SUBMITTED";
        public Task Start(CancellationToken ct) { return Task.FromResult(0); }
        public Task Navigate(string url, CancellationToken ct) { Navigations++; return Task.FromResult(0); }
        public Task<string> Eval(string code, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (code == PortalScripts.Error) return Task.FromResult(Error);
            if (State == "SUBMITTED") Submissions++;
            return Task.FromResult(State);
        }
        public void Dispose() { Disposed = true; }
    }
    private static Settings TestSettings()
    {
        var cfg = new Settings { Account = "TEST_ONLY", Carrier = "联通" };
        cfg.SetPassword("test-only-not-real");
        return cfg;
    }
    private static LoginEngine Engine(FakeBrowser browser, Func<bool> check, bool wifi)
    {
        return new LoginEngine(() => browser, ct => Task.FromResult(check()), (m, w, l, ct) => Task.FromResult(wifi), (ms, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(0); });
    }
    private static async Task Workflow()
    {
        var cfg = TestSettings();
        var b = new FakeBrowser();
        var r = await Engine(b, () => b.Submissions >= 2, true).Run(cfg, true, s => { }, CancellationToken.None);
        Assert(r == LoginResult.Online && b.Submissions == 2 && b.Disposed, "First attempt offline -> second attempt succeeds; browser disposed");
        b = new FakeBrowser();
        r = await Engine(b, () => b.Submissions >= 1, true).Run(cfg, true, s => { }, CancellationToken.None);
        Assert(r == LoginResult.Online && b.Submissions == 1, "First attempt online -> no extra credential submission");
        b = new FakeBrowser();
        r = await Engine(b, () => true, true).Run(cfg, true, s => { }, CancellationToken.None);
        Assert(r == LoginResult.Online && b.Submissions == 0, "Already online -> no login");
        b = new FakeBrowser();
        r = await Engine(b, () => false, true).Run(cfg, true, s => { }, CancellationToken.None);
        Assert(r == LoginResult.Failed && b.Submissions == 2, "Persistent offline -> exactly two submissions maximum");
        b = new FakeBrowser { Error = "AUTH" };
        r = await Engine(b, () => false, true).Run(cfg, true, s => { }, CancellationToken.None);
        Assert(r == LoginResult.NeedsAttention && b.Submissions == 1, "Authentication rejection -> pause without second submission");
        b = new FakeBrowser { State = "CAPTCHA" };
        r = await Engine(b, () => false, true).Run(cfg, true, s => { }, CancellationToken.None);
        Assert(r == LoginResult.NeedsAttention && b.Submissions == 0, "CAPTCHA -> needs manual login");
        b = new FakeBrowser();
        r = await Engine(b, () => false, false).Run(cfg, false, s => { }, CancellationToken.None);
        Assert(r == LoginResult.Skip && b.Navigations == 0, "Wrong Wi-Fi -> no portal opened");
        b = new FakeBrowser { State = "WRONG_PAGE" };
        r = await Engine(b, () => false, true).Run(cfg, false, s => { }, CancellationToken.None);
        Assert(r == LoginResult.Failed && b.Submissions == 0 && b.Navigations == 2, "Untrusted page -> no credentials; direct-page fallback used");
        b = new FakeBrowser();
        using (var c = new CancellationTokenSource())
        {
            c.Cancel();
            try { await Engine(b, () => false, true).Run(cfg, true, s => { }, c.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Assert(b.Disposed && b.Submissions == 0, "Cancelled run disposes browser without submission"); }
        }
    }
    private sealed class LocalServer : IDisposable
    {
        public readonly string Url;
        private readonly HttpListener listener = new HttpListener();
        private readonly Task worker;
        private readonly List<HttpListenerContext> held = new List<HttpListenerContext>();
        public LocalServer()
        {
            var p = new TcpListener(IPAddress.Loopback, 0); p.Start();
            int port = ((IPEndPoint)p.LocalEndpoint).Port; p.Stop();
            Url = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(Url); listener.Start();
            worker = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { break; }
                    string path = context.Request.Url.AbsolutePath;
                    if (path == "/hang") { held.Add(context); continue; }
                    if (path == "/redirect") { context.Response.StatusCode = 302; context.Response.RedirectLocation = Url; context.Response.Close(); continue; }
                    string html = @"<!doctype html><meta charset='utf-8'><h1>中原工学院</h1>
<input name='DDDDD' placeholder='学号'><input name='upass' type='password'>
<select id='carrier'><option value=''>选择运营商</option><option value='@unicom'>中国联通</option><option value='@cmcc'>中国移动</option></select>
<button onclick='window.submitted={account:document.querySelector(""input[name=DDDDD]"").value,password:document.querySelector(""input[type=password]"").value,carrier:document.querySelector(""select"").value};alert(""密码错误"")'>登 录</button>";
                    if (path == "/connecttest") html = "Microsoft Connect Test";
                    byte[] data = Encoding.UTF8.GetBytes(html);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = data.Length;
                    await context.Response.OutputStream.WriteAsync(data, 0, data.Length);
                    context.Response.Close();
                }
            });
        }
        public void Dispose() { listener.Stop(); listener.Close(); try { worker.Wait(2000); } catch { } foreach (var item in held) { try { item.Response.Abort(); } catch { } } }
    }
    private static async Task NetworkProbes()
    {
        using (var server = new LocalServer())
        {
            Assert(await Net.Probe(server.Url + "connecttest", "Microsoft Connect Test", CancellationToken.None), "Connectivity probe accepts expected response");
            Assert(!await Net.Probe(server.Url, "Microsoft Connect Test", CancellationToken.None), "Captive portal HTML does not count as Internet");
            Assert(!await Net.Probe(server.Url + "redirect", "Microsoft Connect Test", CancellationToken.None), "Captive portal redirect does not count as Internet");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert(!await Net.Probe(server.Url + "hang", "Microsoft Connect Test", CancellationToken.None) && watch.Elapsed.TotalSeconds < 7, "Unresponsive asynchronous network probe stops after five seconds");
        }
    }
    private static async Task BrowserIntegration()
    {
        using (var server = new LocalServer())
        using (var timeout = new CancellationTokenSource(45000))
        using (var b = new Browser())
        {
            var ct = timeout.Token;
            await b.Start(ct); await b.Navigate(server.Url, ct);
            string account = "test'\"\\学号", password = "test'\"\\;window.pwned=1;//";
            string state = await b.Eval(PortalScripts.Fill(account, password, "联通", false), ct);
            Assert(state == "WRONG_PAGE", "Real browser refuses credentials on localhost despite matching school title");
            Assert(await b.Eval("String(!!window.submitted)", ct) == "false", "No submission occurred on untrusted page");
            state = await b.Eval(PortalScripts.Fill(account, password, "联通", true), ct);
            Assert(state == "SUBMITTED", "Real browser fills mock Dr.COM form and clicks spaced Chinese login button");
            var json = new JavaScriptSerializer();
            var submitted = json.Deserialize<Dictionary<string, string>>(await b.Eval("JSON.stringify(window.submitted)", ct));
            Assert(submitted["account"] == account && submitted["password"] == password && submitted["carrier"] == "@unicom", "Unicode, quotes, backslashes and carrier option value preserved");
            Assert(await b.Eval("String(!!window.pwned)", ct) == "false", "Credential text cannot inject JavaScript");
            Assert(await b.Eval(PortalScripts.Error, ct) == "AUTH", "Mock portal alert captured and classified as authentication failure");
            await b.Navigate(server.Url, ct);
            await b.Eval("var x=document.createElement('input');x.placeholder='验证码';document.body.appendChild(x);'ok'", ct);
            Assert(await b.Eval(PortalScripts.Fill(account, password, "联通", true), ct) == "CAPTCHA", "Real browser detects CAPTCHA without submitting");
            await b.Navigate(server.Url, ct);
            Assert(await b.Eval(PortalScripts.Fill(account, password, "电信", true), ct) == "CARRIER", "Missing carrier option stops before credentials are filled");
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            var cfg = TestSettings();
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) new XmlSerializer(typeof(Settings)).Serialize(w, cfg);
            Settings roundtrip;
            using (var sr = new StringReader(sb.ToString())) roundtrip = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(sr);
            Assert(roundtrip.Password() == "test-only-not-real" && !sb.ToString().Contains("test-only-not-real"), "DPAPI encrypted settings serialization round trip");
            Assert(Net.ParseSsid("    SSID                   : zut-stu\r\n    BSSID : 01:02:03:04:05:06\r\n") == "zut-stu", "Wi-Fi SSID parsed without BSSID confusion");
            Assert(Net.ParseSsid("    SSID : other-network\r\n") == "other-network", "Other Wi-Fi recognized");
            var schedule = new DailySchedule("08:00", new DateTime(2026, 10, 22, 7, 59, 0));
            Assert(!schedule.TakeDue(new DateTime(2026, 10, 22, 7, 59, 59)) && schedule.TakeDue(new DateTime(2026, 10, 22, 8, 0, 0)) && !schedule.TakeDue(new DateTime(2026, 10, 22, 8, 0, 5)), "Daily login fires once at scheduled time");
            Assert(schedule.TakeDue(new DateTime(2026, 10, 25, 10, 0, 0)) && schedule.Next == new DateTime(2026, 10, 26, 8, 0, 0), "Resume after multiple sleeping days triggers one catch-up, no burst");
            Assert(new DailySchedule("08:00", new DateTime(2026, 10, 22, 9, 0, 0)).Next == new DateTime(2026, 10, 23, 8, 0, 0), "Starting after today's scheduled time schedules tomorrow");
            Workflow().GetAwaiter().GetResult();
            NetworkProbes().GetAwaiter().GetResult();
            BrowserIntegration().GetAwaiter().GetResult();
            Application.EnableVisualStyles();
            Program.Activate = new EventWaitHandle(false, EventResetMode.AutoReset);
            using (var form = new MainForm(false, true))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Show();
                Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.Combine(folder, "ui-preview.png"));
                }
                form.Close();
            }
            Program.Activate.Dispose();
            Assert(true, "Windows Forms UI instantiated and rendered");
            File.WriteAllLines(Path.Combine(folder, "test-results.txt"), passed.ToArray(), Encoding.UTF8);
            foreach (string line in passed) Console.WriteLine(line);
            Console.WriteLine("TOTAL: " + passed.Count + " passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(folder, "test-failure.txt"), ex.ToString()); return 1; }
    }
}
