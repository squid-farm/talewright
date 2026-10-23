import re
import unittest
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
LINK = re.compile(r"!?\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+[^)]*)?\)")
SKIP_DIRS = {".git", ".nx", ".vscode-test", "artifacts", "bin", "node_modules", "obj"}


class DocumentationLinksTests(unittest.TestCase):
    def test_relative_markdown_links_resolve(self):
        broken = []
        for document in ROOT.rglob("*.md"):
            if SKIP_DIRS.intersection(document.relative_to(ROOT).parts):
                continue
            text = document.read_text(encoding="utf-8")
            for match in LINK.finditer(text):
                destination = match.group(1) or match.group(2)
                parsed = urlsplit(destination)
                if parsed.scheme or parsed.netloc or not parsed.path:
                    continue
                if "/" not in parsed.path and "." not in parsed.path:
                    continue
                target = (document.parent / unquote(parsed.path)).resolve()
                if not target.exists():
                    broken.append(f"{document.relative_to(ROOT)}: {destination}")

        self.assertEqual([], broken, "Broken relative Markdown links:\n" + "\n".join(broken))


if __name__ == "__main__":
    unittest.main()
