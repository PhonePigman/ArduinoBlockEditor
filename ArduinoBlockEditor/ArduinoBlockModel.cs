#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArduinoBlockEditor
{
    /// <summary>
    /// ユーザー定義関数のデータ構造
    /// </summary>
    public class UserFunction
    {
        public string Name { get; set; } = "";
        public List<JsonNode> ParametersBlocks { get; set; } = new List<JsonNode>(); // 引数用ブロックリスト
        public List<JsonNode> Blocks { get; set; } = new List<JsonNode>();          // 関数の中身のブロックリスト
    }

    /// <summary>
    /// データ構造の保持、ファイルの入出力、Undo/Redo、コード生成を行うロジッククラス
    /// </summary>
    public class ArduinoBlockModel
    {
        public static readonly List<string> DefaultCategoryOrder = new List<string>
        {
            "入出力・時間",
            "制御構文",
            "演算・計算",
            "変数・代入",
            "関数",
            "シリアル通信",
            "その他"
        };

        public List<JsonNode> BlockConfigs { get; private set; } = new List<JsonNode>();
        public List<string> Categories { get; private set; } = new List<string> { "すべて" };
        public string CurrentFilePath { get; set; } = null;

        public List<JsonNode> SetupBlocks { get; set; } = new List<JsonNode>();
        public List<JsonNode> LoopBlocks { get; set; } = new List<JsonNode>();
        public List<JsonNode> FuncBlocks { get; set; } = new List<JsonNode>();
        public List<UserFunction> UserFunctions { get; set; } = new List<UserFunction>();

        private readonly List<string> undoStack = new List<string>();
        private readonly List<string> redoStack = new List<string>();
        private readonly int maxHistory = 50;
        private bool isUndoRedoAction = false;

        private string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// mods / mod フォルダからJSON設定ファイルを読み込む
        /// </summary>
        public (bool success, string errorMessage) LoadMods()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            BlockConfigs.Clear();
            var targetDirs = new[]
            {
                Path.Combine(BaseDir, "mods"),
                Path.Combine(BaseDir, "mod")
            };

            var foundFiles = new List<string>();
            foreach (var d in targetDirs)
            {
                if (Directory.Exists(d))
                {
                    foundFiles.AddRange(Directory.GetFiles(d, "*.json"));
                }
            }

            if (foundFiles.Count == 0)
            {
                return (false, $"『{BaseDir}』 の中に 'mods' フォルダを作成し、JSONを置いてください。");
            }

            var encodings = new[] { Encoding.UTF8, Encoding.GetEncoding("shift_jis") };

            foreach (var filePath in foundFiles)
            {
                JsonNode data = null;
                foreach (var enc in encodings)
                {
                    try
                    {
                        var jsonText = File.ReadAllText(filePath, enc);
                        data = JsonNode.Parse(jsonText);
                        if (data != null) break;
                    }
                    catch
                    {
                        // 読み込み失敗時はエンコーディングを変えて再試行
                    }
                }

                if (data != null && data["blocks"] is JsonArray blocksArray)
                {
                    foreach (var b in blocksArray)
                    {
                        if (b != null)
                        {
                            BlockConfigs.Add(b.DeepClone());
                        }
                    }
                }
            }

            var cats = new HashSet<string>();
            foreach (var b in BlockConfigs)
            {
                var cat = b["category"]?.ToString() ?? "その他";
                cats.Add(cat);
            }
            cats.Add("変数・代入");
            cats.Add("関数");

            var sortedCats = cats.OrderBy(c =>
            {
                int idx = DefaultCategoryOrder.IndexOf(c);
                return idx >= 0 ? idx : DefaultCategoryOrder.Count;
            }).ThenBy(c => c).ToList();

            Categories = new List<string> { "すべて" };
            Categories.AddRange(sortedCats);

            return (true, string.Empty);
        }

        public bool AddUserFunction(string funcName)
        {
            if (string.IsNullOrWhiteSpace(funcName)) return false;
            if (UserFunctions.Any(f => f.Name == funcName)) return false;

            UserFunctions.Add(new UserFunction { Name = funcName });
            return true;
        }

        public void RemoveUserFunction(string funcName)
        {
            UserFunctions.RemoveAll(f => f.Name == funcName);
        }

        public bool RenameUserFunction(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;
            newName = newName.Trim();
            if (oldName == newName) return true;
            if (UserFunctions.Any(f => f.Name == newName)) return false;

            var target = UserFunctions.FirstOrDefault(f => f.Name == oldName);
            if (target != null)
            {
                target.Name = newName;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 現在のブロック配置状態をUndoスタックに保存
        /// </summary>
        public void SaveState()
        {
            if (isUndoRedoAction) return;

            var stateNode = new JsonObject
            {
                ["setup_blocks"] = JsonSerializer.SerializeToNode(SetupBlocks),
                ["loop_blocks"] = JsonSerializer.SerializeToNode(LoopBlocks),
                ["func_blocks"] = JsonSerializer.SerializeToNode(FuncBlocks),
                ["user_functions"] = JsonSerializer.SerializeToNode(UserFunctions)
            };

            string stateJson = stateNode.ToJsonString();

            if (undoStack.Count > 0 && undoStack.Last() == stateJson)
            {
                return;
            }

            undoStack.Add(stateJson);
            if (undoStack.Count > maxHistory)
            {
                undoStack.RemoveAt(0);
            }

            redoStack.Clear();
        }

        public bool Undo()
        {
            if (undoStack.Count > 1)
            {
                isUndoRedoAction = true;
                string currentState = undoStack.Last();
                undoStack.RemoveAt(undoStack.Count - 1);
                redoStack.Add(currentState);

                string prevStateJson = undoStack.Last();
                RestoreStateFromJson(prevStateJson);

                isUndoRedoAction = false;
                return true;
            }
            return false;
        }

        public bool Redo()
        {
            if (redoStack.Count > 0)
            {
                isUndoRedoAction = true;
                string nextStateJson = redoStack.Last();
                redoStack.RemoveAt(redoStack.Count - 1);
                undoStack.Add(nextStateJson);

                RestoreStateFromJson(nextStateJson);

                isUndoRedoAction = false;
                return true;
            }
            return false;
        }

        private void RestoreStateFromJson(string jsonStr)
        {
            var node = JsonNode.Parse(jsonStr);
            SetupBlocks = JsonSerializer.Deserialize<List<JsonNode>>(node["setup_blocks"]) ?? new List<JsonNode>();
            LoopBlocks = JsonSerializer.Deserialize<List<JsonNode>>(node["loop_blocks"]) ?? new List<JsonNode>();
            FuncBlocks = JsonSerializer.Deserialize<List<JsonNode>>(node["func_blocks"]) ?? new List<JsonNode>();
            UserFunctions = JsonSerializer.Deserialize<List<UserFunction>>(node["user_functions"]) ?? new List<UserFunction>();
        }

        public void ClearAll()
        {
            SetupBlocks.Clear();
            LoopBlocks.Clear();
            FuncBlocks.Clear();
            UserFunctions.Clear();
            CurrentFilePath = null;
        }

        public JsonObject CreateDefaultBlockData(JsonNode config)
        {
            var values = new JsonObject();
            if (config["params"] is JsonArray paramsArr)
            {
                foreach (var p in paramsArr)
                {
                    string key = p["key"]?.ToString();
                    if (!string.IsNullOrEmpty(key))
                    {
                        values[key] = p["default"]?.ToString() ?? "";
                    }
                }
            }

            return new JsonObject
            {
                ["config"] = config.DeepClone(),
                ["values"] = values
            };
        }

        public List<string> GetDeclaredVariables()
        {
            var varsList = new List<string>();
            var allBlocks = SetupBlocks.Concat(LoopBlocks).Concat(FuncBlocks);
            foreach (var uf in UserFunctions)
            {
                allBlocks = allBlocks.Concat(uf.ParametersBlocks).Concat(uf.Blocks);
            }

            void FindVarDefs(IEnumerable<JsonNode> blockList)
            {
                foreach (var b in blockList)
                {
                    if (b is JsonObject obj)
                    {
                        bool isVarDef = obj["config"]?["is_var_def"]?.GetValue<bool>() ?? false;
                        if (isVarDef)
                        {
                            string vname = obj["values"]?["name"]?.ToString().Trim() ?? "";
                            if (!string.IsNullOrEmpty(vname) && !varsList.Contains(vname))
                            {
                                varsList.Add(vname);
                            }
                        }

                        if (obj["values"] is JsonObject valObj)
                        {
                            foreach (var kvp in valObj)
                            {
                                if (kvp.Value is JsonObject subObj)
                                {
                                    FindVarDefs(new[] { subObj });
                                }
                            }
                        }
                    }
                }
            }

            FindVarDefs(allBlocks);
            return varsList;
        }

        public bool LoadProjectFile(string filePath)
        {
            var text = File.ReadAllText(filePath, Encoding.UTF8);
            var data = JsonNode.Parse(text);

            if (data["setup_blocks"] != null && data["loop_blocks"] != null)
            {
                SetupBlocks = JsonSerializer.Deserialize<List<JsonNode>>(data["setup_blocks"]) ?? new List<JsonNode>();
                LoopBlocks = JsonSerializer.Deserialize<List<JsonNode>>(data["loop_blocks"]) ?? new List<JsonNode>();
                FuncBlocks = JsonSerializer.Deserialize<List<JsonNode>>(data["func_blocks"]) ?? new List<JsonNode>();
                UserFunctions = data["user_functions"] != null
                    ? JsonSerializer.Deserialize<List<UserFunction>>(data["user_functions"]) ?? new List<UserFunction>()
                    : new List<UserFunction>();

                CurrentFilePath = filePath;
                return true;
            }
            return false;
        }

        public void SaveProjectFile(string filePath)
        {
            var data = new JsonObject
            {
                ["setup_blocks"] = JsonSerializer.SerializeToNode(SetupBlocks),
                ["loop_blocks"] = JsonSerializer.SerializeToNode(LoopBlocks),
                ["func_blocks"] = JsonSerializer.SerializeToNode(FuncBlocks),
                ["user_functions"] = JsonSerializer.SerializeToNode(UserFunctions)
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(filePath, data.ToJsonString(options), Encoding.UTF8);
            CurrentFilePath = filePath;
        }

        public string EvaluateBlockCode(JsonNode block)
        {
            string template = block["config"]?["template"]?.ToString() ?? "";
            if (block["values"] is JsonObject values)
            {
                foreach (var kvp in values)
                {
                    string k = kvp.Key;
                    var v = kvp.Value;

                    if (v is JsonObject subBlock)
                    {
                        string subCode = EvaluateBlockCode(subBlock);
                        template = template.Replace("{{" + k + "}}", subCode);
                    }
                    else
                    {
                        template = template.Replace("{{" + k + "}}", v?.ToString() ?? "");
                    }
                }
            }
            return template;
        }

        public string GenerateArduinoCode()
        {
            var codeLines = new List<string>();

            // 1. グローバル領域 (func_blocks)
            if (FuncBlocks.Count > 0)
            {
                int indentLevel = 0;
                foreach (var block in FuncBlocks)
                {
                    string line = EvaluateBlockCode(block);
                    if (line.StartsWith("}"))
                    {
                        indentLevel = Math.Max(0, indentLevel - 1);
                    }
                    codeLines.Add(new string(' ', indentLevel * 2) + line);
                    if (line.EndsWith("{"))
                    {
                        indentLevel++;
                    }
                }
                codeLines.Add("\n");
            }

            // 2. setup
            codeLines.Add("void setup() {");
            int setupIndent = 1;
            foreach (var block in SetupBlocks)
            {
                string line = EvaluateBlockCode(block);
                if (line.StartsWith("}"))
                {
                    setupIndent = Math.Max(1, setupIndent - 1);
                }
                codeLines.Add(new string(' ', setupIndent * 2) + line);
                if (line.EndsWith("{"))
                {
                    setupIndent++;
                }
            }
            codeLines.Add("}\n");

            // 3. loop
            codeLines.Add("void loop() {");
            int loopIndent = 1;
            foreach (var block in LoopBlocks)
            {
                string line = EvaluateBlockCode(block);
                if (line.StartsWith("}"))
                {
                    loopIndent = Math.Max(1, loopIndent - 1);
                }
                codeLines.Add(new string(' ', loopIndent * 2) + line);
                if (line.EndsWith("{"))
                {
                    loopIndent++;
                }
            }
            codeLines.Add("}\n");

            // 4. ユーザー定義関数
            foreach (var func in UserFunctions)
            {
                var paramCodes = new List<string>();
                foreach (var pBlock in func.ParametersBlocks)
                {
                    string pCode = EvaluateBlockCode(pBlock).Trim();
                    if (pCode.EndsWith(";")) pCode = pCode.Substring(0, pCode.Length - 1);
                    if (!string.IsNullOrEmpty(pCode))
                    {
                        paramCodes.Add(pCode);
                    }
                }
                string paramsStr = string.Join(", ", paramCodes);

                codeLines.Add($"void {func.Name}({paramsStr}) {{");

                int indentLevel = 1;
                foreach (var block in func.Blocks)
                {
                    string line = EvaluateBlockCode(block);
                    if (line.StartsWith("}"))
                    {
                        indentLevel = Math.Max(1, indentLevel - 1);
                    }
                    codeLines.Add(new string(' ', indentLevel * 2) + line);
                    if (line.EndsWith("{"))
                    {
                        indentLevel++;
                    }
                }
                codeLines.Add("}\n");
            }

            return string.Join("\n", codeLines).TrimEnd();
        }
    }
}