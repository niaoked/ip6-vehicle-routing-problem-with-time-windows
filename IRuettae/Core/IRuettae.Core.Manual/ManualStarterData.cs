﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IRuettae.Core.Manual
{
    public class ManualStarterData : IStarterData
    {
        public (int santaId, int day, int[] visitIds)[] Routes { get; set; }
    }
}