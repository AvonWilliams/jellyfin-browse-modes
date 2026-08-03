#!/usr/bin/env python3
"""Pre-filter and heuristically classify TMDb keywords for Browse Modes v2.0.

Input:  TMDb daily keyword export (JSONL, gzipped)
Output: categorized keyword lists, plus an "uncertain" batch for AI review.
"""

import json
import gzip
import re
import sys
from collections import Counter
from pathlib import Path

# ── Paths ──────────────────────────────────────────────────────────────
INPUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("/tmp/keyword_ids.gz")
OUT_DIR = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("/tmp/keyword_categories")
OUT_DIR.mkdir(exist_ok=True)

# ── Skip patterns (keywords unlikely to make good browse tiles) ────────
SKIP_EXACT = {
    # Technical TMDb patterns
    "aftercreditsstinger", "beforecreditsstinger", "duringcreditsstinger",
    "based on comic", "based on comic book",  # redundant with medium-based
    "special feature", "cameo", "poster", "trailer",
    # Too generic
    "scene", "character", "title", "reference",
}

SKIP_PATTERNS = [
    re.compile(p) for p in [
        r"^\d",                          # starts with digit
        r"\bepisode\b",                  # episode references
        r"\bseason\b",                   # season references
        r"^\d+s$",                       # "1980s", "90s" — but keep for Worlds
        r"remake of",                    # too specific
        r"based on a true",             # handled by "based on true story"
        r"^[a-z]$",                      # single letter
        r"^part \d",                     # "part 1", "part 2"
    ]
]

# ── Category definitions ───────────────────────────────────────────────
# Keywords that STRONGLY match one category via heuristic patterns.

MOOD_KEYWORDS = {
    # Emotional states & aesthetic qualities
    "suspenseful", "dark", "gloomy", "wistful", "intense", "playful",
    "grim", "cheerful", "hopeful", "romantic", "nostalgic", "joyful",
    "tense", "thoughtful", "tragic", "serene", "heartbreaking",
    "inspiring", "uplifting", "melancholic", "whimsical", "quirky",
    "disturbing", "unsettling", "eerie", "soothing", "comforting",
    "bittersweet", "poignant", "lighthearted", "hilarious",
    "thrilling", "exhilarating", "chilling", "terrifying",
    "heartwarming", "sentimental", "surreal", "dreamlike",
    "gritty", "raw", "brutal", "visceral",
    "sad", "funny", "scary", "exciting", "creepy",
    "touching", "moving", "depressing", "uplifting",
    "empowering", "provocative", "contemplative", "meditative",
}

STYLE_KEYWORDS = {
    # Filmmaking techniques, formats, genres-as-style
    "stop motion", "claymation", "rotoscope", "hand drawn animation",
    "3d animation", "2d animation", "computer animation", "traditional animation",
    "silent film", "black and white", "film noir", "neo noir",
    "found footage", "mockumentary", "docufiction",
    "experimental film", "avant garde", "surrealist",
    "musical", "jukebox musical", "rock opera",
    "anime", "manga", "live action adaptation",
    "puppetry", "practical effects", "miniatures",
    "single take", "long take", "tracking shot",
    "split screen", "multiple perspectives",
    "anthology", "ensemble cast", "hyperlink cinema",
    "screwball comedy", "slapstick", "physical comedy",
    "dark comedy", "satire", "parody", "spoof", "farce",
    "melodrama", "soap opera",
    "arthouse", "independent film", "indie",
    "guerrilla filmmaking", "low budget",
    "epic", "blockbuster", "spectacle",
    "gothic", "gothic horror", "gothic romance",
    "body horror", "psychological horror", "cosmic horror", "lovecraftian",
    "slasher", "splatter", "torture porn",
    "film à clef", "roman à clef",
}

# Mood-like emotional words: if a keyword ENDS with these, it's probably a mood
MOOD_SUFFIXES = [
    "ful", "ing", "ous", "ive", "ble", "tic", "cal", "ial", "ate", "ent", "ant",
]

# Keywords containing these words are likely story themes
THEME_SIGNAL_WORDS = [
    "heist", "revenge", "conspiracy", "betrayal", "redemption", "coming of age",
    "time travel", "time loop", "parallel universe", "alternate reality",
    "superhero", "super power", "superhuman",
    "dystopia", "utopia", "post-apocalyptic", "apocalypse",
    "alien invasion", "first contact", "extraterrestrial",
    "artificial intelligence", "robot", "android", "cyborg",
    "virtual reality", "simulation", "matrix",
    "mutant", "mutation", "genetic", "clone",
    "zombie", "vampire", "werewolf", "witch", "wizard", "demon", "ghost",
    "immortal", "reincarnation", "afterlife",
    "curse", "prophecy", "destiny", "fate",
    "quest", "journey", "adventure", "expedition",
    "war", "battle", "revolution", "rebellion", "resistance",
    "spy", "espionage", "undercover", "double agent",
    "mafia", "gangster", "yakuza", "cartel", "triad",
    "prison", "escape", "fugitive", "on the run",
    "survival", "castaway", "stranded",
    "treasure", "gold", "riches",
    "murder", "mystery", "whodunit", "detective", "investigation",
    "serial killer", "psychopath", "sociopath",
    "love story", "romance", "love triangle",
    "family drama", "dysfunctional", "reunion",
    "friendship", "buddy",
    "rise and fall", "rags to riches",
    "good vs evil", "good versus evil",
    "identity", "amnesia", "mistaken identity",
    "swap", "transformation", "metamorphosis",
    "underdog", "triumph", "against all odds",
    "sacrifice", "martyr",
]

# Keywords containing these words are likely plot elements
PLOT_SIGNAL_WORDS = [
    "based on", "inspired by", "adapted from",
    "sequel", "prequel", "spin off", "reboot", "remake",
    "husband wife", "mother daughter", "father son", "brother sister",
    "relationship", "marriage", "divorce", "affair",
    "death of", "loss of", "murder of",
    "flashback", "origin story", "backstory",
    "plot twist", "twist ending", "cliffhanger",
    "voice over", "narration", "framing device",
    "unreliable narrator",
    "training", "montage", "chase", "showdown",
    "wedding", "funeral", "funeral",
    "road trip", "cross country",
    "heist", "robbery", "bank",  # also theme — will need AI to disambiguate
]

# Keywords containing these words are likely worlds/settings
WORLD_SIGNAL_WORDS = [
    "city", "town", "village", "country", "island",
    "desert", "mountain", "forest", "jungle", "ocean", "sea",
    "space", "planet", "moon", "galaxy", "universe",
    "future", "past", "ancient", "medieval", "victorian",
    "school", "college", "university", "hospital", "prison",
    "new york", "los angeles", "london", "paris", "tokyo",
    "winter", "summer", "spring", "autumn",
    "america", "europe", "asia", "africa",
    "castle", "mansion", "farm",
    "suburb", "small town", "rural", "urban",
    "dystopian future", "post apocalyptic world",
    "alternate history", "alternate universe", "parallel world",
    "underground", "underwater", "sky", "heaven", "hell",
    "dungeon", "kingdom", "empire", "realm",
]

def classify_by_heuristic(name_lower: str) -> str | None:
    """Return a category name if heuristic is confident, else None."""

    # Exact matches in curated sets
    if name_lower in MOOD_KEYWORDS:
        return "mood"
    if name_lower in STYLE_KEYWORDS:
        return "style"
    if name_lower in SKIP_EXACT:
        return "skip"

    # Skip patterns
    for pat in SKIP_PATTERNS:
        if pat.search(name_lower):
            return "skip"

    # Strong theme signals — check first since they're most specific
    theme_score = 0
    for signal in THEME_SIGNAL_WORDS:
        if signal in name_lower:
            theme_score += 1
    if theme_score >= 1 and len(name_lower.split()) <= 6:
        return "theme"

    # Strong style signals
    for signal in STYLE_KEYWORDS:
        if signal in name_lower:
            return "style"

    # Strong world signals
    world_score = 0
    for signal in WORLD_SIGNAL_WORDS:
        if signal in name_lower:
            world_score += 1
    if world_score >= 1 and len(name_lower.split()) <= 5:
        return "world"

    # Mood-like endings with short keywords
    words = name_lower.split()
    if len(words) == 1:
        for suffix in MOOD_SUFFIXES:
            if name_lower.endswith(suffix) and len(name_lower) >= 6:
                return "mood"

    return None  # uncertain — needs AI


def main():
    # Load all keywords
    keywords = []
    with gzip.open(INPUT, "rt", encoding="utf-8") as f:
        for line in f:
            kw = json.loads(line)
            keywords.append((kw["id"], kw["name"]))

    print(f"Loaded {len(keywords)} keywords")

    # Classify
    categorized: dict[str, list[tuple[int, str]]] = {
        "mood": [], "theme": [], "plot": [], "world": [], "style": [], "skip": []
    }
    uncertain: list[tuple[int, str]] = []

    for kid, name in keywords:
        name_clean = name.strip()
        if not name_clean or len(name_clean) < 3 or len(name_clean) > 60:
            categorized["skip"].append((kid, name_clean))
            continue

        category = classify_by_heuristic(name_clean.lower())
        if category and category in categorized:
            categorized[category].append((kid, name_clean))
        else:
            uncertain.append((kid, name_clean))

    # Report
    for cat, items in categorized.items():
        print(f"  {cat}: {len(items)}")
    print(f"  uncertain: {len(uncertain)}")

    # Save categorized lists
    for cat, items in categorized.items():
        if cat == "skip":
            continue
        path = OUT_DIR / f"{cat}.json"
        path.write_text(json.dumps(
            [{"id": kid, "name": name} for kid, name in sorted(items, key=lambda x: x[1].lower())],
            indent=2
        ))
        print(f"Wrote {path} ({len(items)} entries)")

    # Save uncertain for AI classification (split into batches of 500)
    uncertain_path = OUT_DIR / "uncertain.json"
    uncertain_path.write_text(json.dumps(
        [{"id": kid, "name": name} for kid, name in uncertain],
        indent=2
    ))
    print(f"Wrote {uncertain_path} ({len(uncertain)} entries)")

    # Also save a compact TSV for bulk AI processing
    tsv_path = OUT_DIR / "uncertain.tsv"
    with open(tsv_path, "w") as f:
        for kid, name in uncertain:
            f.write(f"{kid}\t{name}\n")
    print(f"Wrote {tsv_path}")


if __name__ == "__main__":
    main()
