using System;
using System.Diagnostics;
using System.IO.Compression;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using backend.Infrastructure;

namespace backend.Application;


public class FileProcessing 
{

    public static async Task<string> ProcessFile(string inputPath, List<string> SelectedOutputTypes)
    {
        //unzip file
        var extractPath = Path.Combine(Path.GetTempPath(), "extract", Guid.NewGuid().ToString());
        Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(inputPath, extractPath); 

        //upload file to vector db
        var extractedJsonFiles = Directory.GetFiles(extractPath, "*.json", SearchOption.AllDirectories);
        if (extractedJsonFiles.Length == 0)
            throw new Exception("No .json files found after unzip. Is this the parsed output zip?");

        string chunkDir = SolutionParser.Run(extractPath, Path.Combine(Directory.GetCurrentDirectory(), "parsed_output"));

        var response = "Success";
        List<string> output_files = new List<string>();

        try
        {
            var apiKey = MustEnv("OPENAI_API_KEY");
            // string defaultVs = "vs_6976904735208191b309858e3f2e0f74";
            // string vsId = defaultVs;

            string model = "gpt-5-mini";

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "rag_outputs");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            // start
            
            //vector store setup
            
            var name = "replybrary_chunks";
            var chunks = chunkDir; 
            // var vsId = await CreateVectorStore(http, name);
            // Console.WriteLine($"Vector store: {vsId}");

            var vsId = await CreateVectorStore(http, name);

            Console.WriteLine($"Vector store: {vsId}");

            var files = Directory.GetFiles(chunks!, "*.json", SearchOption.AllDirectories);
            // Console.WriteLine($"Found {files.Length} json files.");

            foreach (var f in files)
            {
                Console.WriteLine($"Uploading: {Path.GetFileName(f)}");
                var fileId = await UploadFile(http, f);
                await AttachFileToVectorStore(http, vsId, fileId);
                Console.WriteLine($"  attached file_id={fileId}");
            }


            for (int i = 0; i < SelectedOutputTypes.Count; i++)
            {
                var rawCmd = SelectedOutputTypes[i].Trim();
                var cmd = rawCmd.ToLowerInvariant();

                Console.WriteLine($"raw='{SelectedOutputTypes}' len={SelectedOutputTypes.Count}");
                Console.WriteLine($"cmd='{cmd}' len={cmd.Length}");
                Console.WriteLine("cmd chars: " + string.Join(",", cmd.Select(c => (int)c)));

                Console.WriteLine("output type: " + cmd);
            
                await WaitForVectorStoreFilesReady(http, vsId);

                // -------- ask --------
                if (cmd == "ask")
                {
                    string? question = null;
                    if (rawCmd.Length > 3)
                    {
                        var sepIndex = rawCmd.IndexOfAny(new[] { ':', '=', '|', '?' });
                        if (sepIndex >= 0 && sepIndex + 1 < rawCmd.Length)
                            question = rawCmd[(sepIndex + 1)..].Trim();
                        else
                            question = rawCmd[3..].Trim();
                    }
                    else if (i + 1 < SelectedOutputTypes.Count)
                    {
                        question = SelectedOutputTypes[i + 1].Trim();
                        i++;
                    }

                    if (string.IsNullOrWhiteSpace(question))
                        throw new Exception("ask requires a question (e.g., \"ask: What is the purpose of this solution?\")");

                    var prompt =
                    $@"Answer using ONLY the uploaded solution chunks.
                    If the information is not present, say 'Not found in uploaded files.'

                    Question:
                    {question}";

                            var answer = await AskWithFileSearch(http, model, vsId, prompt);
                            Console.WriteLine(answer);
                            return response;
                }

                // -------- generate --------
                if (cmd == "overview")
                {
                    var prompt =
                        @"Generate a clean Markdown solution overview based ONLY on the uploaded chunks.
                        Include:
                        - Key counts (workflows, env vars, canvas app groups)
                        - List workflows
                        - List environment variables (names)
                        - Keep it concise, headings + bullet points.";

                    var md = await AskWithFileSearch(http, model, vsId, prompt);
                    var path = Path.Combine(outDir, "overview.md");
                    File.WriteAllText(path, md, Encoding.UTF8);
                    Console.WriteLine($"Wrote: {path}");

                    // export to Word
                    var wordPath = Path.Combine(outDir, "Replybrary_Overview.docx");
                    RunProcess("pandoc", $"\"{path}\" -o \"Replybrary_Overview.docx\" --toc", outDir);

                    Console.WriteLine($"Wrote: {wordPath}");
                    return wordPath;
                }

                if (cmd == "workflows")
                {
                    var prompt =
                        @"Summarise each workflow in Markdown using ONLY the uploaded chunks.
                        For each workflow include:
                        - Workflow name
                        - What it does (1–3 lines)
                        - Any obvious trigger/purpose if available
                        If missing details, say 'Not found in uploaded files.'";

                    Console.WriteLine("Generating workflows.md...");

                    var md = await AskWithFileSearch(http, model, vsId, prompt);
                    var path = Path.Combine(outDir, "workflows.md");

                    File.WriteAllText(path, md, Encoding.UTF8);

                    var wordPath = Path.Combine(outDir, "Replybrary_Workflows.docx");
                    RunProcess("pandoc", $"\"{path}\" -o \"Replybrary_Workflows.docx\" --toc", outDir);

                    Console.WriteLine($"Wrote: {wordPath}");
                    // return wordPath;
                    output_files.Add(wordPath);
                }

                if (cmd == "faq")
                {
                    var prompt =
                        @"Create a Markdown FAQ for the solution using ONLY the uploaded chunks.
                        Include ~10 Q&As (workflows, env vars, canvas apps, what the solution contains).
                        If info is missing, say 'Not found in uploaded files.' Keep it concise.";

                    var md = await AskWithFileSearch(http, model, vsId, prompt);
                    var path = Path.Combine(outDir, "faq.md");
                    Console.WriteLine($"Wrote: {path}");

                    File.WriteAllText(path, md, Encoding.UTF8);

                    var wordPath = Path.Combine(outDir, "Replybrary_FAQ.docx");
                    RunProcess("pandoc", $"\"{path}\" -o \"Replybrary_FAQ.docx\" --toc", outDir);
                    Console.WriteLine($"Wrote: {wordPath}");
                    // return wordPath;
                    output_files.Add(wordPath);
                }


                if (cmd == "diagrams")
                {
                    var prompt =
                        @"Output ONLY ONE Mermaid diagram (no explanation), using flowchart LR.

                        It MUST include all three groups as explicit nodes:
                        1) Canvas Apps (both apps as nodes)
                        2) Workflows (each workflow as its own node — do NOT use a single 'Workflows' hub node)
                        3) Environment Variables (each env var as its own node)

                        Connections rules:
                        - Connect each Canvas App node to each Workflow node (high-level relationship).
                        - Connect each Workflow node to a hub node named: Environment Variables (shared)
                        - Connect that hub node to EVERY environment variable node.
                        - Do NOT invent per-workflow env var mappings unless explicitly stated in uploaded chunks.

                        Formatting rules:
                        - Use subgraphs named exactly: CanvasApps, Workflows, EnvironmentVariables
                        - Use safe IDs:
                        - CA1, CA2 for canvas apps
                        - W1..W10 for workflows
                        - EVH for env var hub
                        - E1..E16 for env vars
                        - Labels must use the real names from the uploaded chunks.
                        - Output ONLY valid Mermaid code. No second diagram. No markdown fences.";

                    var mermaid = await AskWithFileSearch(http, model, vsId, prompt);

                    mermaid = mermaid.Replace("```mermaid", "").Replace("```", "").Trim();

                    var path = Path.Combine(outDir, "architecture.mmd");
                    File.WriteAllText(path, mermaid, Encoding.UTF8);
                    Console.WriteLine($"Wrote: {path}");
                        // return path;
                    output_files.Add(path);
                }

                if (cmd == "environment-variables")
                {
                    var prompt =
                        @"Create a json response for the solution using ONLY the specified structure to 
                        capture environment variables and details such as type, description, name of the variable in Dev Environment, name of the variable in UAT Environment, name of the variable in Production Environment.
                        For description fields, use brief text (1-2 sentences) taken from the uploaded chunks.
                        If any information is missing like name, type, dev_value, test_value, or production_value, don't assume, use null for that field.
                        Structure:
                        [
                        {
                            ""Name"": ""<Environment Variable Name>"",
                            ""Type"": ""<Type>"",
                            ""Description"": ""<Description>"",
                            ""DevValue"": ""<Dev Value - Name of Dev Environment - DEV>"",
                            ""TestValue"": ""<Test Value - Name of UAT Environment - UAT>"",
                            ""ProductionValue"": ""<Production Value - Name of Production Environment>""
                        },
                        ...
                        ]
                        ";

                    Console.WriteLine("Generating environment-variables.xlsx...");

                    var json_res = await AskWithFileSearch(http, model, vsId, prompt);
                    //construct table 

                    var parsedJsonRes = JsonSerializer.Deserialize<HashSet<EnvironmentVariableValue>>(json_res);
                    Console.WriteLine(parsedJsonRes.GetType());

                    foreach (var item in parsedJsonRes)
                    {
                        try
                        {
                            var envVar = JsonSerializer.Deserialize<EnvironmentVariableValue>(item.ToString());
                            if (envVar != null)
                            {
                                if (envVar.DevValue == null)
                                {
                                    envVar.DevValue = "<not specified>";
                                }

                                if (envVar.TestValue == null)
                                {
                                    envVar.TestValue = "<not specified>";
                                }

                                if (envVar.ProductionValue == null)
                                {
                                    envVar.ProductionValue = "<not specified>";
                                }

                                parsedJsonRes.Add(envVar);
                            }
                        }
                        catch (JsonException)
                        {
                            Console.WriteLine($"Failed to parse item: {item}");
                        }
                    }

                    //loop through parsedJsonRes for me
                    foreach (var envVar in parsedJsonRes)
                    {
                        Console.WriteLine($"Name: {envVar.Name}");
                        Console.WriteLine($"Type: {envVar.Type}");
                        Console.WriteLine($"Description: {envVar.Description}");
                        Console.WriteLine($"Dev Value: {envVar.DevValue}");
                        Console.WriteLine($"Test Value: {envVar.TestValue}");
                        Console.WriteLine($"Production Value: {envVar.ProductionValue}");
                        Console.WriteLine();
                    }

                    var excel_path = Path.Combine(outDir, "environment-variables.xlsx");
                    Diagram.construct_table(parsedJsonRes, excel_path); 

                    // return excel_path;
                    output_files.Add(excel_path);

                }

                throw new Exception("Unknown generate type. Use: overview | workflows | faq | diagrams");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return "failed: " + ex.Message;
            //Environment.Exit(1);
            
        }
        //TODO: temp
        return "failed";
        
    }

    static async Task WaitForVectorStoreFilesReady(HttpClient http, string vsId)
    {
        for (int i = 0; i < 600; i++) // up to 10 mins
        {
            var res = await http.GetAsync($"https://api.openai.com/v1/vector_stores/{vsId}/files");
            var json = await ReadJson(res);

            var data = json.GetProperty("data");
            bool anyFailed = false;
            bool allCompleted = true;

            foreach (var item in data.EnumerateArray())
            {
                var st = item.GetProperty("status").GetString();
                if (st == "failed") anyFailed = true;
                if (st != "completed") allCompleted = false;
            }

            if (anyFailed) throw new Exception("At least one file failed indexing.");
            if (allCompleted && data.GetArrayLength() > 0) return;

            await Task.Delay(1000);
        }

        throw new Exception("Vector store files not ready after waiting.");
    }

    static string MustEnv(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
            throw new Exception($"Missing env var: {name}");
        return v!;
    }

    static async Task<JsonElement> ReadJson(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)res.StatusCode}:\n{text}");
        return JsonDocument.Parse(text).RootElement;
    }

    static async Task<string> CreateVectorStore(HttpClient http, string name)
    {
        var body = JsonSerializer.Serialize(new { name });
        var res = await http.PostAsync(
            "https://api.openai.com/v1/vector_stores",
            new StringContent(body, Encoding.UTF8, "application/json")
        );
        var json = await ReadJson(res);
        return json.GetProperty("id").GetString()!;
    }

    static async Task<string> UploadFile(HttpClient http, string path)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("assistants"), "purpose");

        await using var fs = File.OpenRead(path);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");

        form.Add(fileContent, "file", Path.GetFileName(path));

        var res = await http.PostAsync("https://api.openai.com/v1/files", form);
        var json = await ReadJson(res);
        return json.GetProperty("id").GetString()!;
    }

    static async Task AttachFileToVectorStore(HttpClient http, string vectorStoreId, string fileId)
    {
        var body = JsonSerializer.Serialize(new { file_id = fileId });
        var res = await http.PostAsync(
            $"https://api.openai.com/v1/vector_stores/{vectorStoreId}/files",
            new StringContent(body, Encoding.UTF8, "application/json")
        );
        Console.WriteLine($"Attaching file {fileId} to vector store {vectorStoreId}");
        _ = await ReadJson(res);
    }

    static async Task<string> AskWithFileSearch(HttpClient http, string model, string vectorStoreId, string prompt)
    {

        //TODO: temp
        var response = "Success";

        var payload = new
        {
            model,
            input = prompt,
            tools = new object[]
            {
                new {
                    type = "file_search",
                    vector_store_ids = new[] { vectorStoreId }
                }
            }
        };

        var body = JsonSerializer.Serialize(payload);
        var res = await http.PostAsync(
            "https://api.openai.com/v1/responses",
            new StringContent(body, Encoding.UTF8, "application/json")
        );

        var json = await ReadJson(res);

        if (json.TryGetProperty("output_text", out var ot) &&
            ot.ValueKind == JsonValueKind.String)
            return ot.GetString()!;

        if (json.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) &&
                    t.GetString() == "message" &&
                    item.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in content.EnumerateArray())
                    {
                        if (c.TryGetProperty("type", out var ct) &&
                            ct.GetString() == "output_text" &&
                            c.TryGetProperty("text", out var tx))
                            return tx.GetString()!;
                    }
                }
            }
        }

        return json.ToString();
    }

    static int RunProcess(string fileName, string arguments, string workingDir)
    {
        var p = new Process();
        p.StartInfo.FileName = fileName;
        p.StartInfo.Arguments = arguments;
        p.StartInfo.WorkingDirectory = workingDir;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.UseShellExecute = false;

        p.Start();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
        if (p.ExitCode != 0)
            throw new Exception($"Command failed: {fileName} {arguments}\n{stderr}");

        return p.ExitCode;
    }

}
