// Lista da Vez - servidor local (sem dependências, .NET Framework 4)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public class Registro
{
    public long id { get; set; }
    public string data { get; set; }      // yyyy-MM-dd
    public string hora { get; set; }      // HH:mm
    public string vendedora { get; set; }
    public string compra { get; set; }    // VD | NVD | TR
    public string origem { get; set; }    // PSS | RD | WL | IND
    public string cliente { get; set; }
    public string obs { get; set; }
    public string alteradoEm { get; set; }
}

public class Banco
{
    public List<Registro> registros { get; set; }
    public List<string> vendedoras { get; set; }
    public long proximoId { get; set; }
    public Banco() { registros = new List<Registro>(); vendedoras = new List<string>(); proximoId = 1; }
}

public static class Servidor
{
    const int PORTA = 3000;
    static readonly object trava = new object();
    static Banco banco;
    static string pasta = AppDomain.CurrentDomain.BaseDirectory;
    static string arquivo = Path.Combine(pasta, "dados.json");
    static JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static string html;
    static long versao = DateTime.Now.Ticks;

    // ---------- atualização automática via GitHub Releases ----------
    public const string VERSAO = "1.0.1";                 // altere a cada release (tag v1.0.1)
    const string REPO = "pridema1/listavezpalhador";

    static void Atualizar()
    {
        var exe = Assembly.GetExecutingAssembly().Location;
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); } catch { }
        if (REPO.StartsWith("SEU_")) return;
        try
        {
            Console.WriteLine("Verificando atualizações...");
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            var wc = new WebClient();
            wc.Headers.Add("User-Agent", "ListaDaVez");
            var rel = json.Deserialize<Dictionary<string, object>>(
                wc.DownloadString("https://api.github.com/repos/" + REPO + "/releases/latest"));
            var tag = rel["tag_name"].ToString().TrimStart('v', 'V');
            if (new Version(tag) <= new Version(VERSAO)) { Console.WriteLine("Versão " + VERSAO + " já é a mais recente."); return; }

            string url = null;
            foreach (Dictionary<string, object> a in (System.Collections.ArrayList)rel["assets"])
                if (a["name"].ToString().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) url = a["browser_download_url"].ToString();
            if (url == null) return;

            Console.WriteLine("Baixando versão " + tag + "...");
            var novo = exe + ".new";
            wc.Headers.Add("User-Agent", "ListaDaVez");
            wc.DownloadFile(url, novo);
            if (new FileInfo(novo).Length < 10000) { File.Delete(novo); return; }
            File.Move(exe, exe + ".old");   // Windows permite renomear o exe em execução
            File.Move(novo, exe);
            Console.WriteLine("Atualizado! Reiniciando...");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = pasta, UseShellExecute = true });
            Environment.Exit(0);
        }
        catch (Exception e) { Console.WriteLine("Não foi possível atualizar (seguindo com a versão atual): " + e.Message); }
    }

    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Lista da Vez - Servidor v" + VERSAO;
        Carregar();
        Atualizar();
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
        using (var r = new StreamReader(s, Encoding.UTF8)) html = r.ReadToEnd();

        TcpListener ouvinte;
        try { ouvinte = new TcpListener(IPAddress.Any, PORTA); ouvinte.Start(); }
        catch (Exception e)
        {
            Console.WriteLine("Não foi possível abrir a porta " + PORTA + ". O servidor já está aberto?\n" + e.Message);
            AbrirNavegador("http://localhost:" + PORTA);
            Thread.Sleep(4000); return;
        }

        Console.WriteLine("==============================================");
        Console.WriteLine("  LISTA DA VEZ - servidor rodando");
        Console.WriteLine("==============================================");
        Console.WriteLine("  Neste PC:      http://localhost:" + PORTA);
        foreach (var ip in IPsLocais()) Console.WriteLine("  Na rede (cel): http://" + ip + ":" + PORTA);
        Console.WriteLine("\n  Dados salvos em: " + arquivo);
        Console.WriteLine("  NÃO FECHE ESTA JANELA enquanto a loja estiver aberta.");
        Console.WriteLine("==============================================");

        AbrirNavegador("http://localhost:" + PORTA);

        while (true)
        {
            var cli = ouvinte.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(_ => { try { Atender(cli); } catch { } finally { cli.Close(); } });
        }
    }

    static IEnumerable<string> IPsLocais()
    {
        return Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString());
    }

    static void AbrirNavegador(string url) { try { Process.Start(url); } catch { } }

    // ---------- persistência ----------
    static void Carregar()
    {
        if (File.Exists(arquivo))
        {
            try { banco = json.Deserialize<Banco>(File.ReadAllText(arquivo, Encoding.UTF8)); }
            catch { File.Copy(arquivo, arquivo + ".corrompido-" + DateTime.Now.ToString("yyyyMMddHHmmss")); }
        }
        if (banco == null) banco = new Banco();
        if (banco.registros == null) banco.registros = new List<Registro>();
        if (banco.vendedoras == null) banco.vendedoras = new List<string>();
    }

    static void Salvar()
    {
        versao = DateTime.Now.Ticks;
        var tmp = arquivo + ".tmp";
        File.WriteAllText(tmp, json.Serialize(banco), Encoding.UTF8);
        if (File.Exists(arquivo)) File.Replace(tmp, arquivo, null); else File.Move(tmp, arquivo);
        // backup diário
        var dirBk = Path.Combine(pasta, "backups");
        Directory.CreateDirectory(dirBk);
        File.Copy(arquivo, Path.Combine(dirBk, "dados-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json"), true);
    }

    // ---------- HTTP ----------
    static void Atender(TcpClient cli)
    {
        cli.ReceiveTimeout = 10000;
        var ns = cli.GetStream();
        var cab = new MemoryStream();
        int b, fim = 0;
        while ((b = ns.ReadByte()) != -1)
        {
            cab.WriteByte((byte)b);
            fim = (b == '\r' || b == '\n') ? fim + 1 : 0;
            if (fim == 4) break;
            if (cab.Length > 16384) return;
        }
        var linhas = Encoding.ASCII.GetString(cab.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var partes = linhas[0].Split(' ');
        if (partes.Length < 2) return;
        string metodo = partes[0], alvo = partes[1];
        int tam = 0;
        foreach (var l in linhas)
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(15).Trim(), out tam);
        var corpoB = new byte[tam]; int lidos = 0;
        while (lidos < tam) { int n = ns.Read(corpoB, lidos, tam - lidos); if (n <= 0) break; lidos += n; }
        string corpo = Encoding.UTF8.GetString(corpoB, 0, lidos);

        string caminho = alvo, query = "";
        int qi = alvo.IndexOf('?');
        if (qi >= 0) { caminho = alvo.Substring(0, qi); query = alvo.Substring(qi + 1); }
        var q = new Dictionary<string, string>();
        foreach (var kv in query.Split('&'))
        {
            if (kv == "") continue;
            var p = kv.Split(new[] { '=' }, 2);
            q[Uri.UnescapeDataString(p[0])] = p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "";
        }

        int status = 200; string tipo = "application/json; charset=utf-8"; string resp; string extra = "";
        try
        {
            resp = Rotear(metodo, caminho, q, corpo, ref status, ref tipo, ref extra);
        }
        catch (Exception e) { status = 400; resp = json.Serialize(new { erro = e.Message }); }

        var bytes = Encoding.UTF8.GetBytes(resp);
        var h = "HTTP/1.1 " + status + " OK\r\nContent-Type: " + tipo + "\r\nContent-Length: " + bytes.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n" + extra + "\r\n";
        var hb = Encoding.ASCII.GetBytes(h);
        ns.Write(hb, 0, hb.Length); ns.Write(bytes, 0, bytes.Length); ns.Flush();
    }

    static string Txt(Dictionary<string, object> d, string k, int max)
    {
        object v; if (!d.TryGetValue(k, out v) || v == null) return "";
        var s = v.ToString().Trim(); return s.Length > max ? s.Substring(0, max) : s;
    }

    static string Rotear(string m, string c, Dictionary<string, string> q, string corpo, ref int st, ref string tipo, ref string extra)
    {
        if (m == "GET" && (c == "/" || c == "/index.html")) { tipo = "text/html; charset=utf-8"; return html; }

        if (m == "GET" && c == "/api/versao") return json.Serialize(new { versao = versao });

        if (m == "GET" && c == "/api/estado")
        {
            string de = q.ContainsKey("de") ? q["de"] : DateTime.Now.ToString("yyyy-MM-dd");
            string ate = q.ContainsKey("ate") ? q["ate"] : de;
            lock (trava)
            {
                var regs = banco.registros.Where(r => string.CompareOrdinal(r.data, de) >= 0 && string.CompareOrdinal(r.data, ate) <= 0)
                    .OrderBy(r => r.data).ThenBy(r => r.hora).ThenBy(r => r.id).ToList();
                return json.Serialize(new { versao = versao, registros = regs, vendedoras = banco.vendedoras, ips = IPsLocais().ToList(), porta = PORTA });
            }
        }

        if (m == "GET" && c == "/api/csv")
        {
            string de = q["de"], ate = q["ate"];
            var sb = new StringBuilder("\uFEFFData;Hora;Vendedora;Comprou;Origem;Cliente;Observacoes\r\n");
            lock (trava)
                foreach (var r in banco.registros.Where(r => string.CompareOrdinal(r.data, de) >= 0 && string.CompareOrdinal(r.data, ate) <= 0)
                             .OrderBy(r => r.data).ThenBy(r => r.hora))
                    sb.Append(string.Join(";", new[] { r.data, r.hora, r.vendedora, r.compra, r.origem, r.cliente, r.obs }
                        .Select(x => "\"" + (x ?? "").Replace("\"", "\"\"").Replace("\n", " ") + "\""))).Append("\r\n");
            tipo = "text/csv; charset=utf-8";
            extra = "Content-Disposition: attachment; filename=\"lista-da-vez_" + de + "_" + ate + ".csv\"\r\n";
            return sb.ToString();
        }

        var d = string.IsNullOrEmpty(corpo) ? new Dictionary<string, object>() : json.Deserialize<Dictionary<string, object>>(corpo);

        if (m == "POST" && c == "/api/registros")
        {
            var agora = DateTime.Now;
            var r = new Registro
            {
                data = agora.ToString("yyyy-MM-dd"), hora = agora.ToString("HH:mm"),
                vendedora = Txt(d, "vendedora", 60), compra = Txt(d, "compra", 3), origem = Txt(d, "origem", 3),
                cliente = Txt(d, "cliente", 100), obs = Txt(d, "obs", 500)
            };
            if (r.vendedora == "") throw new Exception("Informe a vendedora");
            lock (trava) { r.id = banco.proximoId++; banco.registros.Add(r); Salvar(); }
            return json.Serialize(r);
        }

        if ((m == "PUT" || m == "DELETE") && c.StartsWith("/api/registros/"))
        {
            long id = long.Parse(c.Substring(15));
            lock (trava)
            {
                var r = banco.registros.FirstOrDefault(x => x.id == id);
                if (r == null) { st = 404; return "{\"erro\":\"não encontrado\"}"; }
                if (m == "DELETE") banco.registros.Remove(r);
                else
                {
                    // hora e data não podem ser alteradas (evita discussões)
                    if (d.ContainsKey("vendedora") && Txt(d, "vendedora", 60) != "") r.vendedora = Txt(d, "vendedora", 60);
                    if (d.ContainsKey("compra")) r.compra = Txt(d, "compra", 3);
                    if (d.ContainsKey("origem")) r.origem = Txt(d, "origem", 3);
                    if (d.ContainsKey("cliente")) r.cliente = Txt(d, "cliente", 100);
                    if (d.ContainsKey("obs")) r.obs = Txt(d, "obs", 500);
                    r.alteradoEm = DateTime.Now.ToString("dd/MM HH:mm");
                }
                Salvar();
                return json.Serialize(r);
            }
        }

        if (m == "POST" && c == "/api/vendedoras")
        {
            var nome = Txt(d, "nome", 60);
            if (nome == "") throw new Exception("Nome vazio");
            lock (trava) { if (!banco.vendedoras.Contains(nome)) { banco.vendedoras.Add(nome); banco.vendedoras.Sort(); Salvar(); } }
            return "{}";
        }
        if (m == "DELETE" && c == "/api/vendedoras")
        {
            lock (trava) { banco.vendedoras.Remove(q["nome"]); Salvar(); }
            return "{}";
        }

        st = 404; return "{\"erro\":\"rota inexistente\"}";
    }
}
