import copy
import glob
import json
import os
import sys

def get_base_dir():
    if getattr(sys, "frozen", False):
        return os.path.dirname(sys.executable)
    else:
        return os.path.dirname(os.path.abspath(__file__))

BASE_DIR = get_base_dir()

DEFAULT_CATEGORY_ORDER = [
    "入出力・時間",
    "制御構文",
    "演算・計算",
    "変数・代入",
    "シリアル通信",
    "その他",
]

class ArduinoBlockModel:
    """データ構造の保持、ファイルの入出力、Undo/Redo、コード生成を行うロジッククラス"""
    def __init__(self):
        self.block_configs = []
        self.categories = ["すべて"]
        self.current_filepath = None

        self.setup_blocks = []
        self.loop_blocks = []
        self.func_blocks = []

        self.undo_stack = []
        self.redo_stack = []
        self.max_history = 50
        self.is_undo_redo_action = False

    def load_mods(self):
        target_dirs = [
            os.path.join(BASE_DIR, "mods"),
            os.path.join(BASE_DIR, "mod"),
        ]
        found_files = []
        for d in target_dirs:
            if os.path.exists(d):
                found_files.extend(glob.glob(os.path.join(d, "*.json")))

        if not found_files:
            return False, f"『{BASE_DIR}』 の中に 'mods' フォルダを作成し、JSONを置いてください。"

        for file_path in found_files:
            data = None
            for encoding in ["utf-8", "utf-8-sig", "cp932", "shift_jis"]:
                try:
                    with open(file_path, "r", encoding=encoding) as f:
                        data = json.load(f)
                    break
                except (UnicodeDecodeError, json.JSONDecodeError):
                    continue

            if data and "blocks" in data:
                for b in data["blocks"]:
                    self.block_configs.append(b)

        cats = set()
        for b in self.block_configs:
            cats.add(b.get("category", "その他"))
        cats.add("変数・代入")

        def get_cat_sort_key(cat):
            if cat in DEFAULT_CATEGORY_ORDER:
                return (DEFAULT_CATEGORY_ORDER.index(cat), cat)
            return (len(DEFAULT_CATEGORY_ORDER), cat)

        sorted_cats = sorted(list(cats), key=get_cat_sort_key)
        self.categories = ["すべて"] + sorted_cats
        return True, ""

    def save_state(self):
        if self.is_undo_redo_action:
            return

        state = {
            "setup_blocks": copy.deepcopy(self.setup_blocks),
            "loop_blocks": copy.deepcopy(self.loop_blocks),
            "func_blocks": copy.deepcopy(self.func_blocks),
        }

        if self.undo_stack and self.undo_stack[-1] == state:
            return

        self.undo_stack.append(state)
        if len(self.undo_stack) > self.max_history:
            self.undo_stack.pop(0)

        self.redo_stack.clear()

    def undo(self):
        if len(self.undo_stack) > 1:
            self.is_undo_redo_action = True
            current_state = self.undo_stack.pop()
            self.redo_stack.append(current_state)

            prev_state = copy.deepcopy(self.undo_stack[-1])
            self.setup_blocks = prev_state["setup_blocks"]
            self.loop_blocks = prev_state["loop_blocks"]
            self.func_blocks = prev_state.get("func_blocks", [])
            self.is_undo_redo_action = False
            return True
        return False

    def redo(self):
        if self.redo_stack:
            self.is_undo_redo_action = True
            next_state = self.redo_stack.pop()
            self.undo_stack.append(next_state)

            self.setup_blocks = copy.deepcopy(next_state["setup_blocks"])
            self.loop_blocks = copy.deepcopy(next_state["loop_blocks"])
            self.func_blocks = copy.deepcopy(next_state.get("func_blocks", []))
            self.is_undo_redo_action = False
            return True
        return False

    def clear_all(self):
        self.setup_blocks.clear()
        self.loop_blocks.clear()
        self.func_blocks.clear()
        self.current_filepath = None

    def create_default_block_data(self, config):
        values = {}
        for p in config.get("params", []):
            values[p["key"]] = p.get("default", "")
        return {"config": config, "values": values}

    def get_declared_variables(self):
        vars_list = []
        all_blocks = self.setup_blocks + self.loop_blocks + self.func_blocks

        def find_var_defs(block_list):
            for b in block_list:
                if isinstance(b, dict):
                    if b.get("config", {}).get("is_var_def"):
                        vname = b["values"].get("name", "").strip()
                        if vname and vname not in vars_list:
                            vars_list.append(vname)
                    for v in b.get("values", {}).values():
                        if isinstance(v, dict):
                            find_var_defs([v])

        find_var_defs(all_blocks)
        return vars_list

    def load_project_file(self, filepath):
        with open(filepath, "r", encoding="utf-8") as f:
            data = json.load(f)

        if "setup_blocks" in data and "loop_blocks" in data:
            self.setup_blocks = data["setup_blocks"]
            self.loop_blocks = data["loop_blocks"]
            self.func_blocks = data.get("func_blocks", [])
            self.current_filepath = filepath
            return True
        return False

    def save_project_file(self, filepath):
        data = {
            "setup_blocks": self.setup_blocks,
            "loop_blocks": self.loop_blocks,
            "func_blocks": self.func_blocks,
        }
        with open(filepath, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        self.current_filepath = filepath

    def evaluate_block_code(self, block):
        template = block["config"]["template"]
        values = block["values"]

        for k, v in values.items():
            if isinstance(v, dict):
                sub_code = self.evaluate_block_code(v)
                template = template.replace(f"{{{{{k}}}}}", sub_code)
            else:
                template = template.replace(f"{{{{{k}}}}}", str(v))

        return template

    def generate_arduino_code(self):
        code_lines = []

        if self.func_blocks:
            indent_level = 0
            for block in self.func_blocks:
                line = self.evaluate_block_code(block)
                if line.startswith("}"):
                    indent_level = max(0, indent_level - 1)
                code_lines.append("  " * indent_level + line)
                if line.endswith("{"):
                    indent_level += 1
            code_lines.append("\n")

        code_lines.append("void setup() {")
        indent_level = 1
        for block in self.setup_blocks:
            line = self.evaluate_block_code(block)
            if line.startswith("}"):
                indent_level = max(1, indent_level - 1)
            code_lines.append("  " * indent_level + line)
            if line.endswith("{"):
                indent_level += 1
        code_lines.append("}\n")

        code_lines.append("void loop() {")
        indent_level = 1
        for block in self.loop_blocks:
            line = self.evaluate_block_code(block)
            if line.startswith("}"):
                indent_level = max(1, indent_level - 1)
            code_lines.append("  " * indent_level + line)
            if line.endswith("{"):
                indent_level += 1
        code_lines.append("}")

        return "\n".join(code_lines)