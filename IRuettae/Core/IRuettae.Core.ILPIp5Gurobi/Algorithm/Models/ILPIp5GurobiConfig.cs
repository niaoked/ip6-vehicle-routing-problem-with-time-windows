﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IRuettae.Core.ILPIp5Gurobi.Algorithm.Models
{
    /// <summary>
    /// Class that holds the ilp specific data belonging to a route calculation
    /// </summary>
    public class ILPIp5GurobiConfig : ISolverConfig
    {
        // General
        public int TimeSliceDuration { get; set; }

        // Phase 1
        public double ClusteringMIPGap { get; set; }
        public long ClusteringTimeLimitMiliseconds { get; set; }

        // Phase 2
        public double SchedulingMIPGap { get; set; }
        public long SchedulingTimeLimitMiliseconds { get; set; }
    }
}
