using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ZLogger
{
    public interface IAsyncLogProcessor : IAsyncDisposable
    {
        [HideInCallstack]
        void Post(IZLoggerEntry log);
    }
}
