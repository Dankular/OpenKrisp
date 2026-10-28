"""Small CPU language-identification model; no generative call for English gating."""
import re
from functools import lru_cache


@lru_cache(maxsize=1)
def identifier():
    from langid.langid import LanguageIdentifier, model
    return LanguageIdentifier.from_modelstring(model, norm_probs=True)


@lru_cache(maxsize=128)
def foreign_language(text):
    # Sentence-sized context avoids classifying English around a foreign passage.
    pieces=re.split(r'(?<=[.!?])\s+|\n+',text)
    for piece in reversed(pieces):
        words=piece.split()
        if len(words)<2: continue
        language,probability=identifier().classify(' '.join(words[-50:]))
        if language!='en' and probability>=.9: return language
    return ''
