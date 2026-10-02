#!/usr/bin/env python3
"""Turns failed tests in .trx result files into GitHub annotations and a job summary.

Usage: test-report.py <results-dir>
Each failed test becomes an "::error" line (shown in the run's Annotations panel) with its
message and the top of its stack trace, and a table is appended to $GITHUB_STEP_SUMMARY.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def escape(text: str) -> str:
    # Workflow command escaping for message data.
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def main() -> int:
    directory = sys.argv[1] if len(sys.argv) > 1 else "TestResults"
    failures = []
    total = passed = 0
    for path in sorted(glob.glob(os.path.join(directory, "**", "*.trx"), recursive=True)):
        root = ET.parse(path).getroot()
        for result in root.iterfind(".//t:UnitTestResult", NS):
            total += 1
            outcome = result.get("outcome")
            if outcome == "Passed":
                passed += 1
                continue
            if outcome != "Failed":
                continue
            name = result.get("testName", "?")
            message = (result.findtext("t:Output/t:ErrorInfo/t:Message", "", NS) or "").strip()
            stack = (result.findtext("t:Output/t:ErrorInfo/t:StackTrace", "", NS) or "").strip()
            output = (result.findtext("t:Output/t:StdOut", "", NS) or "").strip()
            failures.append((name, message, stack, output))

    for name, message, stack, output in failures:
        detail = message + ("\n" + "\n".join(stack.splitlines()[:6]) if stack else "")
        print(f"::error title=Test failed: {escape(name)}::{escape(detail)}")

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as out:
            out.write(f"## Tests: {passed} passed, {len(failures)} failed, {total} total\n\n")
            for name, message, stack, output in failures:
                out.write(f"### ❌ {name}\n\n```\n{message}\n\n{stack}\n```\n\n")
                if output:
                    out.write(f"<details><summary>Test output</summary>\n\n```\n{output[-4000:]}\n```\n</details>\n\n")
    print(f"{passed} passed, {len(failures)} failed, {total} total")
    return 0


if __name__ == "__main__":
    sys.exit(main())
