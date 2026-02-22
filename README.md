# discoteka

*the media player for the post-streaming era*

discoteka is a local music player inspired by WinAmp, foobar2000, and iTunes. What makes it unique is its ability to import library XML files from Apple Music/iTunes and Rekordbox, see which songs in your library are actually available as local files, and which ones are streaming only.

distoteka was built because I have a lot of music in Apple Music streaming that I want to get locally so I can use my old iPods again. (and just be less reliant on flaky internet/streaming services in general)

### discoteka is still a very early alpha.

some basic functionality is still being worked on, and the UI needs polish. if you want to use discoteka, I appreciate meaningful bug reports, but understand that updates will be rapid in this early stage. discoteka does not have a built-in updater, so check back regularly for updates!

# building

you will need the .NET 10 SDK and libVLC. (this should be included for Windows, but Linux users will have to install this from your system package manager.) clone the repo, navigate to the solution folder, and run `dotnet build`. you can also go to the discoteka/ folder and use `dotnet run` if you want console log output.

# contributing

not currently accepting pull requests, but feature requests and bug reports are welcomed! 

# license - GPLv3 and what that means to you

discoteka is free software under GPLv3.

you are free to use, modify, and redistribute it, even commercially.

if you distribute modified versions, you must also make your source code available under GPLv3.

this ensures discoteka and its improvements remain open and available to everyone.

no warranty is provided.

# ai disclosure

discoteka is made with AI assistance to accelerate development. a more detailed write-up in "adventures in vibe coding" will be available on my website later.
