"""Every key that crosses the video graph is declared on `AgentState`.

LangGraph keeps a channel per declared key and silently drops the rest — in the
input, and in what a node returns. `shot_timeline` (the shot picker's tiles and
stills) and `previous_aligned` / `previous_sources` (the shots a refine keeps)
were written, read, and unit-tested node by node, and lost in every real
render: the picker showed numbered tiles from the plan, and every refine
re-searched every shot. No test ran the real graph, and none could here — so
this checks the source instead, which needs no LangGraph at all.
"""

import ast
from pathlib import Path
from typing import get_type_hints

from app.video_agent.state import AgentState

PKG = Path(__file__).resolve().parents[1] / "app" / "video_agent"
DECLARED = set(get_type_hints(AgentState))

#: Variables that hold graph state: nodes read `state`, rehydrate builds the
#: seed as `state`, the worker reads the result as `final`.
STATE_NAMES = {"state", "final"}


def _key(node: ast.AST) -> str | None:
    return node.value if isinstance(node, ast.Constant) and isinstance(node.value, str) else None


def _returned_keys(tree: ast.AST) -> dict[str, list[str]]:
    """function name -> keys of the dict literals it returns."""
    out: dict[str, list[str]] = {}
    for fn in ast.walk(tree):
        if isinstance(fn, (ast.FunctionDef, ast.AsyncFunctionDef)):
            for ret in ast.walk(fn):
                if isinstance(ret, ast.Return) and isinstance(ret.value, ast.Dict):
                    out.setdefault(fn.name, []).extend(
                        k for k in (_key(k) for k in ret.value.keys if k is not None) if k)
    return out


def _keys_used(tree: ast.AST) -> dict[str, int]:
    """key -> first line it is read or written through a state variable."""
    found: dict[str, int] = {}
    returns = _returned_keys(tree)

    def add(key: str | None, line: int) -> None:
        if key is not None:
            found.setdefault(key, line)

    for node in ast.walk(tree):
        # state["k"], final["k"] — read or assigned
        if isinstance(node, ast.Subscript) and isinstance(node.value, ast.Name) \
                and node.value.id in STATE_NAMES:
            add(_key(node.slice), node.lineno)
        # state.get("k"), final.get("k")
        elif isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) \
                and node.func.attr == "get" and isinstance(node.func.value, ast.Name) \
                and node.func.value.id in STATE_NAMES and node.args:
            add(_key(node.args[0]), node.lineno)
        # state.update({...}) / state.update(helper(...)) — seeding in bulk
        elif isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) \
                and node.func.attr == "update" and isinstance(node.func.value, ast.Name) \
                and node.func.value.id == "state" and node.args:
            arg = node.args[0]
            if isinstance(arg, ast.Dict):
                for k in arg.keys:
                    add(_key(k) if k is not None else None, node.lineno)
            elif isinstance(arg, ast.Call) and isinstance(arg.func, ast.Name):
                for k in returns.get(arg.func.id, []):
                    add(k, node.lineno)
        # {**state, "k": ...} — what a node hands back to the graph
        elif isinstance(node, ast.Dict) and any(
                k is None and isinstance(v, ast.Name) and v.id == "state"
                for k, v in zip(node.keys, node.values)):
            for k in node.keys:
                add(_key(k) if k is not None else None, node.lineno)
        # state = {"k": ...} — the seed a refine starts from
        elif isinstance(node, (ast.Assign, ast.AnnAssign)):
            targets = node.targets if isinstance(node, ast.Assign) else [node.target]
            if any(isinstance(t, ast.Name) and t.id == "state" for t in targets) \
                    and isinstance(node.value, ast.Dict):
                for k in node.value.keys:
                    add(_key(k) if k is not None else None, node.lineno)
        # graph.invoke({...}) — the input (not tool.invoke, a search call)
        elif isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) \
                and node.func.attr == "invoke" and isinstance(node.func.value, ast.Name) \
                and node.func.value.id == "graph" and node.args \
                and isinstance(node.args[0], ast.Dict):
            for k in node.args[0].keys:
                add(_key(k) if k is not None else None, node.lineno)
    return found


def test_every_key_through_the_graph_is_declared() -> None:
    undeclared = []
    for path in sorted(PKG.glob("*.py")):
        for key, line in _keys_used(ast.parse(path.read_text())).items():
            if key not in DECLARED:
                undeclared.append(f"{path.name}:{line} {key!r}")
    assert not undeclared, (
        "Used through the graph state but not declared on AgentState — LangGraph "
        "drops these silently:\n  " + "\n  ".join(undeclared)
    )


def test_the_keys_that_went_missing_are_declared() -> None:
    assert {"shot_timeline", "previous_aligned", "previous_sources"} <= DECLARED


def test_the_check_sees_each_way_a_key_is_used() -> None:
    """Guards the guard: each pattern above is actually recognised."""
    src = '''
def node(state):
    a = state["read_sub"]
    b = state.get("read_get")
    return {**state, "written": 1}

def seed():
    state = {"seeded": 1}
    state["assigned"] = 2
    final = graph.invoke({"input_key": 1})
    tool.invoke({"not_state": 1})
    state.update({"updated": 1})
    state.update(helper())
    return final.get("final_get")

def helper():
    return {"from_helper": 1}
'''
    assert set(_keys_used(ast.parse(src))) == {
        "read_sub", "read_get", "written", "seeded", "assigned", "input_key", "final_get",
        "updated", "from_helper",
    }
