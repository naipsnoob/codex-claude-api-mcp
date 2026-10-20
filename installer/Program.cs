using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexClaudeApiMcpSetup;

internal static class Program
{
    private const string DefaultEndpoint = "https://claude-ss.ia.br";
    private const string Model = "claude-opus-5";
    private const string ServerName = "claude_external";
    private const string InstallMarker = "codex-claude-api-mcp-install.txt";

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--selfcheck", StringComparer.OrdinalIgnoreCase))
        {
            try { SelfCheck(); return 0; }
            catch (Exception error) { Console.Error.WriteLine("Installer selfcheck failed: " + error.Message); return 1; }
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Codex + Claude API MCP Setup";
        Console.WriteLine("Codex + Claude API MCP Setup");
        Console.WriteLine("Este instalador valida a API e configura o MCP local do Codex. Não precisa de administrador.");
        Console.WriteLine();

        if (!OperatingSystem.IsWindows()) return Fail("Este instalador executável foi criado para Windows.");
        if (Console.IsInputRedirected) return Fail("Abra o executável em um terminal interativo para inserir a chave com entrada oculta.");

        var node = FindOnPath("node.exe");
        var npm = node is null ? null : Path.Combine(Path.GetDirectoryName(node)!, "npm.cmd");
        if (node is null || !File.Exists(npm))
            return Fail("Node.js e npm não foram encontrados. Instale Node.js LTS e execute este instalador novamente.");

        Console.Write("URL base ou endpoint /v1/messages [" + DefaultEndpoint + "]: ");
        var endpointInput = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(endpointInput)) endpointInput = DefaultEndpoint;

        Uri baseUri;
        try { baseUri = NormalizeBaseUri(endpointInput); }
        catch (Exception error) { return Fail("URL inválida: " + error.Message); }

        Console.Write("API key (entrada oculta): ");
        var apiKey = ReadSecret();
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("A API key não pode estar vazia.");

        try
        {
            Console.WriteLine();
            Console.WriteLine("Validando endpoint e acesso ao modelo " + Model + "...");
            var validation = await VerifyEndpointAsync(baseUri, apiKey);
            if (!validation.Success) return Fail(validation.Message);
            Console.WriteLine("API OK — HTTP 200; tokens desta verificação: entrada " + validation.InputTokens + ", saída " + validation.OutputTokens + ".");

            try
            {
                Install(baseUri, apiKey, npm!);
                Console.WriteLine();
                Console.WriteLine("Instalação OK. O MCP foi configurado no Codex.");
                Console.WriteLine("Reinicie o Codex para carregar o servidor claude_external.");
                Console.WriteLine("Modelo: " + Model + " | esforço: high | max_tokens: 128000");
                return 0;
            }
            catch (Exception error)
            {
                return Fail("A API respondeu, mas a instalação não terminou: " + error.Message);
            }
        }
        finally
        {
            apiKey = string.Empty;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("ERRO: " + message);
        return 1;
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ReadSecret()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        return value.ToString().Trim();
    }

    internal static Uri NormalizeBaseUri(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException("informe uma URL absoluta.");
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("use HTTPS para proteger a API key durante o envio.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("não inclua usuário, senha, query ou fragmento na URL.");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)) path = path[..^"/v1/messages".Length];
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^"/v1".Length];
        var builder = new UriBuilder(uri) { Path = path, Query = "", Fragment = "" };
        return builder.Uri;
    }

    private static async Task<ValidationResult> VerifyEndpointAsync(Uri baseUri, string apiKey)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/v1/messages"));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = 16,
            messages = new[] { new { role = "user", content = "Reply with OK." } },
        }), Encoding.UTF8, "application/json");

        try
        {
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var retry = response.Headers.RetryAfter?.Delta?.ToString() ?? response.Headers.RetryAfter?.Date?.ToString("O");
                var suffix = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retry is not null ? " Retry-After: " + retry + "." : "";
                return new(false, "A API retornou HTTP " + (int)response.StatusCode + " (" + response.StatusCode + ")." + suffix, 0, 0);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            if (!json.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return new(false, "HTTP 200, mas a resposta não corresponde ao formato Messages API da Anthropic.", 0, 0);
            var usage = json.RootElement.TryGetProperty("usage", out var usageElement) ? usageElement : default;
            var input = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var inValue) ? inValue.GetInt32() : 0;
            var output = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens", out var outValue) ? outValue.GetInt32() : 0;
            return new(true, "", input, output);
        }
        catch (TaskCanceledException)
        {
            return new(false, "A validação expirou após 90 segundos; verifique o endpoint e tente novamente manualmente.", 0, 0);
        }
        catch (HttpRequestException error)
        {
            return new(false, "Não foi possível conectar ao endpoint: " + error.Message, 0, 0);
        }
        catch (JsonException)
        {
            return new(false, "HTTP 200, mas a resposta não era JSON válido da Messages API.", 0, 0);
        }
    }

    private static void Install(Uri baseUri, string apiKey, string npmPath)
    {
        var mcpParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "mcp");
        var installPath = Path.Combine(mcpParent, "claude-external-api-mcp");
        var markerPath = Path.Combine(installPath, InstallMarker);
        var existingInstall = Directory.Exists(installPath);
        if (existingInstall && !File.Exists(markerPath))
            throw new IOException("O diretório de destino já existe e não foi criado por este instalador; nada foi sobrescrito: " + installPath);

        Directory.CreateDirectory(mcpParent);
        var stagePath = Path.Combine(mcpParent, ".claude-external-api-mcp-stage-" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        try
        {
            Directory.CreateDirectory(stagePath);
            if (existingInstall) CopyOwnedFiles(installPath, stagePath);
            else ExtractRuntime(stagePath);

            if (!existingInstall)
            {
                var exitCode = RunNpmInstall(npmPath, stagePath);
                if (exitCode != 0) throw new InvalidOperationException("npm ci terminou com exit code " + exitCode + ". Verifique internet e Node.js.");
                File.WriteAllText(Path.Combine(stagePath, InstallMarker), "codex-claude-api-mcp\n", new UTF8Encoding(false));
            }

            var plaintext = Encoding.UTF8.GetBytes(apiKey);
            try
            {
                var secret = Dpapi.Protect(plaintext);
                try { File.WriteAllBytes(Path.Combine(stagePath, "api-key.dpapi"), secret); }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            File.WriteAllText(Path.Combine(stagePath, "settings.json"), JsonSerializer.Serialize(new
            {
                base_url = baseUri.AbsoluteUri.TrimEnd('/'),
                model = Model,
            }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));

            if (existingInstall)
            {
                foreach (var file in Directory.EnumerateFiles(stagePath)) File.Copy(file, Path.Combine(installPath, Path.GetFileName(file)), overwrite: true);
                Directory.Delete(stagePath, recursive: true);
            }
            else Directory.Move(stagePath, installPath);

            var launcher = Path.Combine(installPath, "launch.ps1");
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell não foi encontrado.", powershell);
            var configText = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
            var updatedConfig = ConfigWriter.Upsert(configText, ServerName, powershell, launcher);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var tempConfig = configPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempConfig, updatedConfig, new UTF8Encoding(false));
            File.Move(tempConfig, configPath, overwrite: true);
        }
        finally
        {
            var stageFullPath = Path.GetFullPath(stagePath);
            var parentPrefix = Path.GetFullPath(mcpParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Directory.Exists(stageFullPath) && stageFullPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(stageFullPath).StartsWith(".claude-external-api-mcp-stage-", StringComparison.Ordinal))
                Directory.Delete(stageFullPath, recursive: true);
        }
    }

    private static void ExtractRuntime(string destination)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexClaudeApiMcpSetup.mcp-runtime.zip")
            ?? throw new InvalidOperationException("O executável não contém o pacote MCP embutido.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O pacote MCP contém um caminho inválido.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var source = entry.Open();
            using var target = File.Create(output);
            source.CopyTo(target);
        }
    }

    private static void CopyOwnedFiles(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            CopyOwnedFiles(directory, target);
        }
    }

    private static int RunNpmInstall(string npmPath, string directory)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var commandProcessor = Path.Combine(systemRoot, "System32", "cmd.exe");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = "/d /c \"\"" + npmPath + "\" ci --omit=dev --ignore-scripts --no-audit --no-fund\"",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        process.StartInfo.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "PATHEXT" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) process.StartInfo.Environment[name] = value;
        }
        process.StartInfo.Environment["PATH"] = Path.GetDirectoryName(npmPath) + Path.PathSeparator + Path.Combine(systemRoot, "System32");
        process.StartInfo.Environment["ComSpec"] = commandProcessor;
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var output = (stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult()).Trim();
            var tail = string.Join(Environment.NewLine, output.Split(Environment.NewLine).TakeLast(8));
            Console.Error.WriteLine(tail);
        }
        return process.ExitCode;
    }

    private static void SelfCheck()
    {
        Assert(NormalizeBaseUri("https://example.test").AbsoluteUri.TrimEnd('/') == "https://example.test", "base URL normalization");
        Assert(NormalizeBaseUri("https://example.test/v1").AbsoluteUri.TrimEnd('/') == "https://example.test", "versioned base URL normalization");
        Assert(NormalizeBaseUri("https://example.test/v1/messages").AbsoluteUri.TrimEnd('/') == "https://example.test", "full endpoint normalization");
        try { NormalizeBaseUri("http://example.test"); throw new InvalidOperationException("HTTP endpoint was unexpectedly accepted."); }
        catch (ArgumentException) { }

        const string oldConfig = "# keep this\n[mcp_servers.other]\ncommand = \"node\"\nargs = []\n\n[mcp_servers.claude_external]\ncommand = \"old\"\nargs = []\nenv = { CLAUDE_SS_API_KEY = \"fake-secret\" }\n\n[mcp_servers.last]\ncommand = \"last\"\n";
        var updated = ConfigWriter.Upsert(oldConfig, ServerName, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"C:\Users\test\.codex\mcp\launch.ps1");
        Assert(updated.Contains("[mcp_servers.other]", StringComparison.Ordinal), "preserve unrelated config");
        Assert(updated.Contains("[mcp_servers.last]", StringComparison.Ordinal), "preserve later config");
        Assert(!updated.Contains("fake-secret", StringComparison.Ordinal), "remove previous inline key");
        Assert(updated.Split("[mcp_servers.claude_external]", StringSplitOptions.None).Length == 2, "one target config section");

        var marker = Encoding.UTF8.GetBytes("dpapi-selfcheck-marker");
        var protectedBytes = Dpapi.Protect(marker);
        var restored = Dpapi.Unprotect(protectedBytes);
        Assert(Encoding.UTF8.GetString(restored) == "dpapi-selfcheck-marker", "DPAPI current-user round trip");
        CryptographicOperations.ZeroMemory(restored);
        CryptographicOperations.ZeroMemory(protectedBytes);
        Console.WriteLine("Installer selfcheck passed: URL normalization, config preservation/secret removal, and Windows DPAPI round trip.");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Selfcheck failed: " + label);
    }

    private sealed record ValidationResult(bool Success, string Message, int InputTokens, int OutputTokens);
}

internal static class ConfigWriter
{
    internal static string Upsert(string original, string serverName, string powershellPath, string launcherPath)
    {
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var header = "[mcp_servers." + serverName + "]";
        var start = lines.FindIndex(line => line.Trim().Equals(header, StringComparison.Ordinal));
        var end = start;
        if (start >= 0)
        {
            end = start + 1;
            while (end < lines.Count)
            {
                var candidate = lines[end].Trim();
                if (candidate.StartsWith('[') && candidate.EndsWith(']') && !candidate.StartsWith("[mcp_servers." + serverName + ".", StringComparison.Ordinal)) break;
                end++;
            }
        }

        var section = new[]
        {
            header,
            "command = " + Quote(powershellPath),
            "args = [\"-NoProfile\", \"-ExecutionPolicy\", \"Bypass\", \"-File\", " + Quote(launcherPath) + "]",
        };
        if (start >= 0) lines.RemoveRange(start, end - start);
        else
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            start = lines.Count;
        }
        lines.InsertRange(start, section);
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}

internal static class Dpapi
{
    private const uint UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    internal static byte[] Unprotect(byte[] protectedData) => Transform(protectedData, protect: false);

    private static byte[] Transform(byte[] inputBytes, bool protect)
    {
        var inputMemory = Marshal.AllocHGlobal(inputBytes.Length);
        Marshal.Copy(inputBytes, 0, inputMemory, inputBytes.Length);
        var input = new DataBlob { Size = inputBytes.Length, Data = inputMemory };
        DataBlob output = default;
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "Codex Claude API MCP key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or unprotect the API key.");
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, output.Size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(inputMemory);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
