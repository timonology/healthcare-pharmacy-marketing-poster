"""No module uses a name it never defines or imports.

The refine classifier shipped calling `anthropic_chat`, whose import had been
pasted into the module docstring. Python accepted the file; the name only failed
when called, inside a `try` written to swallow provider outages — so it looked
like an outage, for every request, and no test covered the path. pyflakes would
have caught it, but it is not a dependency, so this is the same check in the
standard library: every free name in every function must be bound somewhere at
module level or be a builtin.
"""

import ast
import builtins
import symtable
from pathlib import Path

APP = Path(__file__).resolve().parents[1] / "app"
_MODULE_DUNDERS = {"__file__", "__name__", "__doc__", "__spec__", "__path__", "__package__"}


def _bound_at_module(tree: ast.Module) -> set[str]:
    names: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            names.add(node.name)
        elif isinstance(node, (ast.Import, ast.ImportFrom)):
            names.update((a.asname or a.name).split(".")[0] for a in node.names)
        elif isinstance(node, ast.Global):
            names.update(node.names)
        elif isinstance(node, ast.Name) and isinstance(node.ctx, (ast.Store, ast.Del)):
            names.add(node.id)
    return names


def _undefined(path: Path) -> list[str]:
    source = path.read_text()
    tree = ast.parse(source)
    if any(isinstance(n, ast.ImportFrom) and any(a.name == "*" for a in n.names)
           for n in ast.walk(tree)):
        return []  # a star import could define anything
    known = _bound_at_module(tree) | set(dir(builtins)) | _MODULE_DUNDERS
    found: list[str] = []

    def walk(table: symtable.SymbolTable) -> None:
        for sym in table.get_symbols():
            if (sym.is_referenced() and not sym.is_assigned() and not sym.is_imported()
                    and (sym.is_global() or table.get_type() == "module")
                    and sym.get_name() not in known):
                found.append(f"{table.get_name()}: {sym.get_name()}")
        for child in table.get_children():
            walk(child)

    walk(symtable.symtable(source, str(path), "exec"))
    return found


def test_every_name_the_service_uses_is_defined() -> None:
    problems = {
        str(p.relative_to(APP.parent)): names
        for p in sorted(APP.rglob("*.py"))
        if (names := _undefined(p))
    }
    assert problems == {}, f"undefined names (a misplaced import?): {problems}"


def test_the_check_catches_an_import_hidden_in_a_docstring(tmp_path: Path) -> None:
    # The exact shape of the bug, so this test cannot pass by checking nothing.
    bad = tmp_path / "bad.py"
    bad.write_text('"""Doc.\nfrom app.llm import anthropic_chat\n"""\n\n'
                   "def go():\n    return anthropic_chat()\n")

    assert _undefined(bad) == ["go: anthropic_chat"]
