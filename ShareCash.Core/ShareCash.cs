using System;
using System.IO.MemoryMappedFiles;

namespace ShareCash.Core
{
    public class ShareCash
    {
        public const int UserInteractionCheckInterval = 5 * 1000;
        public const string UserInteractionSignalPath = @"c:\sharecash\UserInteractionSignaler.txt";
    }
}
