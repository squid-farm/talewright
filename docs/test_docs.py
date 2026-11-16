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

    def test_assistant_topics_are_directly_discoverable_and_do_not_fan_out(self):
        entry = ROOT / "docs/guide/talemark-agent-brief.md"
        topics = entry.parent / "talemark"
        linked_topics = set()
        for match in LINK.finditer(entry.read_text(encoding="utf-8")):
            destination = match.group(1) or match.group(2)
            if destination.startswith("talemark/"):
                linked_topics.add((entry.parent / destination).resolve())

        self.assertEqual({path.resolve() for path in topics.glob("*.md")}, linked_topics)
        self.assertTrue(linked_topics)
        for topic in sorted(linked_topics):
            with self.subTest(topic=topic.name):
                text = topic.read_text(encoding="utf-8")
                prose = re.sub(r"`[^`\n]+`", "", text)
                self.assertEqual([], LINK.findall(prose), "Topic pages must not require another documentation hop.")
                self.assertEqual(1, text.count("```yaml\n"))
                self.assertEqual(1, text.count("```tale\n"))


if __name__ == "__main__":
    unittest.main()
