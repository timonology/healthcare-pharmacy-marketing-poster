"""LangChain chain that suggests changes to a canvas document."""

from functools import lru_cache

from langchain_core.output_parsers import StrOutputParser
from langchain_core.prompts import ChatPromptTemplate
from langchain_core.runnables import Runnable
from langchain_openai import AzureChatOpenAI

from app.config import get_settings
from app.schemas.canvas import CanvasDocument

_SYSTEM = """You are a canvas-design assistant.
The user will give you a JSON-encoded canvas document and a natural-language
instruction. Reply with concrete, ordered suggestions referencing shape ids.
Do not invent shapes that aren't present.
"""


@lru_cache
def _llm() -> AzureChatOpenAI:
    s = get_settings()
    return AzureChatOpenAI(
        azure_endpoint=s.azure_openai_endpoint or "",
        api_key=s.azure_openai_api_key or "",  # type: ignore[arg-type]
        api_version=s.azure_openai_api_version,
        azure_deployment=s.azure_openai_deployment,
        temperature=0.2,
    )


@lru_cache
def get_chain() -> Runnable:
    prompt = ChatPromptTemplate.from_messages(
        [
            ("system", _SYSTEM),
            (
                "human",
                "Canvas summary:\n{summary}\n\nInstruction:\n{instruction}",
            ),
        ]
    )
    return prompt | _llm() | StrOutputParser()


async def suggest_edits(doc: CanvasDocument, instruction: str) -> str:
    chain = get_chain()
    return await chain.ainvoke(
        {"summary": doc.to_summary(), "instruction": instruction}
    )
