﻿using System.Collections.Generic;
using System.IO;

namespace ProjectHoppy.Core.Content
{
    public abstract class ContentLoader
    {
        //public abstract IEnumerable<string> FileSignatures { get; }
        public abstract object Load(Stream stream);
    }
}
