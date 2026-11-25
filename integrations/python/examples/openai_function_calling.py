"""
ReLearn via raw OpenAI / Azure OpenAI function calling — no LangChain, no MCP.

    pip install openai
    set RELEARN_CLI=dotnet C:\\path\\to\\relearn.dll
    python examples/openai_function_calling.py C:\\src\\Legacy\\Legacy.sln java-spring C:\\src\\modernization

For Azure OpenAI replace the client with:
    from openai import AzureOpenAI
    client = AzureOpenAI(azure_endpoint=..., api_key=..., api_version="2024-10-21")
and pass your deployment name as MODEL.
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from openai import OpenAI  # noqa: E402

from relearn_tools import call_tool, openai_tools, system_prompt  # noqa: E402

MODEL = os.environ.get("RELEARN_MODEL", "gpt-4.1")
solution, target, out = sys.argv[1], sys.argv[2], sys.argv[3]

client = OpenAI()
messages = [
    {"role": "system", "content": system_prompt()},
    {"role": "user", "content": f"Relearn {solution} for target {target}; write outputs to {out}. Run all phases."},
]

for _ in range(200):  # hard cap on agent turns
    resp = client.chat.completions.create(model=MODEL, messages=messages, tools=openai_tools())
    msg = resp.choices[0].message
    messages.append(msg.model_dump(exclude_none=True))
    if not msg.tool_calls:
        print(msg.content)
        break
    for tc in msg.tool_calls:
        print(f"→ {tc.function.name}({tc.function.arguments[:120]})")
        result = call_tool(tc.function.name, tc.function.arguments)
        messages.append({"role": "tool", "tool_call_id": tc.id, "content": result})
else:
    print("Stopped: turn limit reached")

