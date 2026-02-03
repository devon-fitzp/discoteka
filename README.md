# discoteka
*a music library and player for the post-streaming world*

Inspired by music libraries and players like foobar2000, Winamp, or iTunes (before Apple Music). A media library for audiophiles, DJs, curators, and collectors alike.

Import your streaming libraries (currently only Apple Music supported, more to come) and scan your local media. See what you have in streaming that you don't have locally, or filter your lowest quality files. See the different formats you have your music available in - FLAC, MP3, M4A, WAV, OGG, ALAC... 

Import your Rekordbox library (VirtualDJ, Traktor, and Serato to come) in case the tracks you spin aren't the tracks you're listening to. Sort your libraries into cross-ecosystem playlists to sync back to an iPod or other media player.

Written in C# and built for Linux first. Windows builds will also be available. macOS support is being considered but not prioritized. 

---

# WARNINGS

1. This is an *extremely* early version - most things *do not work* yet, and the program *will probably* crash on you. *I'm working on it.*
2. This program is being written with AI assistance. For more information, see the section AI Disclosure below.

---

## AI Disclosure

I am building discoteka with heavy reliance on Codex by OpenAI. I'm able to read and write code, but using AI to move much quicker. As an example, the core of discoteka revolves around a SQLite database. Given a reference manual for the .NET SQLite bindings and a quick cheatsheet on SQL, I could implement my database-import routines by hand. 

It would take me about 4 hours to do so. 

Alternately, I can define my schema and tell Codex what I want to accomplish, and have exactly the same code I'd end up with, but done in 5 minutes. 

There is still a human - me - driving the project design: how it needs to look, what it needs to do, and how it's going to do that. I'm using Codex as, effectively, a translator from plain English to C#. I can describe and understand the program flow and structure, but can use AI to iterate quicker and focus on the parts of the program design that actually matter. 

For the record: I'm still largely anti-generative-AI when used in cases of plagiarizing human creative work. AI "art" will never be art. But there are still useful cases and applications of "AI" - largely holdovers from when we still called it "Machine Learning". 

An additional note: I am considering adding AI/LLM/ML features to discoteka. They will be implemented in a meaningful, deliberate way for function *I* actually want, and they will be designed as opt-*IN* so you never have to enable them if you don't want them. 
