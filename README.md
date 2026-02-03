# discoteka
*a music library and player for the post-streaming world*

Inspired by music libraries and players like foobar2000, Winamp, or iTunes (before Apple Music). A media library for audiophiles, DJs, curators, and collectors alike.

Import your streaming libraries (currently only Apple Music supported, more to come) and scan your local media. See what you have in streaming that you don't have locally, or filter your lowest quality files. See the different formats you have your music available in - FLAC, MP3, M4A, WAV, OGG, ALAC... 

Import your Rekordbox library (VirtualDJ, Traktor, and Serato to come) in case the tracks you spin aren't the tracks you're listening to. Sort your libraries into cross-ecosystem playlists to sync back to an iPod or other media player.

Written in C# and built for Linux first. Windows builds will also be available. macOS support is being considered but not prioritized. 

---

# WARNING

This is an *extremely* early version - most things *do not work* yet, and the program *will probably* crash on you. 

---

## AI Disclosure

**discoteka is built with the assistance of AI tools, primarily OpenAI Codex.**

I can read, write, and reason about code independantly, but I use AI to dramatically speed up development. In practice, Codex acts as a translator from plain English to C#, allowing me to iterate much faster while keeping full control over the project's design and behavior.

For example, discoteka's core revolves around a SQLite database. Given the .NET SQLite bindings documentation and a SQL reference guide, I could implement the required import routines entirely by hand. Doing so would take several hours. By instead defining the schema and clearly designing the intended behavior, I can arrive at functionally identical, readable code in minutes.

The key point is that a human - me - is still driving the project: the architecture, the data models, user experience, and technical decisions. AI is used as an acceleration tool, much like IntelliSense, line completion, and linters before. It's not an autonomous author. I review, understand, and integrate all generated code, and I remain responsible for the final result. 

Allow me to be explicit: I am *strongly* opposed to generative AI when it's used to plagiarize or replace human creative work. AI-generated "art" is not art. That said, there are still legitimate uses for these technologies, what we historically called "machine learning" - automation, analysis, and tooling to help humans work more efficiently and effectively. 

Finally: discoteka may include optional AI / ML / LLM-powered features in the future. Any such features will be purposeful - features you'd actually want to use, not just "AI for the sake of AI" - and strictly opt-in. You will never be required to enable AI-backed features nor consent to data training to use discoteka. 
