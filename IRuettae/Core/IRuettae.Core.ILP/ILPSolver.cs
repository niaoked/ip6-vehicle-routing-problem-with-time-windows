﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IRuettae.Core.ILP.Algorithm;
using IRuettae.Core.ILP.Algorithm.Clustering.TargetFunctionBuilders;
using IRuettae.Core.Models;
using IRuettae.Preprocessing.Mapping;
using ResultState = IRuettae.Core.ILP.Algorithm.ResultState;
using Route = IRuettae.Core.Models.Route;
using Waypoint = IRuettae.Core.Models.Waypoint;

namespace IRuettae.Core.ILP
{
    public class ILPSolver : ISolver
    {
        private readonly OptimisationInput input;

        public ILPSolver(OptimisationInput input)
        {
            this.input = input;
        }

        public OptimizationResult Solve(int timelimit, IProgress<int> progress)
        {
            var sw = Stopwatch.StartNew();
            
            int timeslice = 5 * 60; // 5 mins

            var clusteringSolverVariableBuilder = new ClusteringSolverVariableBuilder(input, 0);
            var clusteringSolverInputData = clusteringSolverVariableBuilder.Build();
            var clusterinSolver =
                new Algorithm.Clustering.Solver(clusteringSolverInputData, new MinAvgTimeTargetFunctionBuilder());
            var phase1ResultState = clusterinSolver.Solve(timeslice, timelimit);
            if (!(new[] { ResultState.Feasible, ResultState.Optimal }).Contains(phase1ResultState))
            {
                return null;
            }

            var phase1Result = clusterinSolver.GetResult();
            progress.Report(50);


            var schedulingSovlerVariableBuilders = new List<SchedulingSolverVariableBuilder>();
            foreach (var santa in Enumerable.Range(0, phase1Result.Waypoints.GetLength(0)))
            {
                foreach (var day in Enumerable.Range(0, phase1Result.Waypoints.GetLength(1)))
                {
                    var cluster = phase1Result.Waypoints[santa, day];
                    var schedulingOptimisationInput = new OptimisationInput
                    {
                        Visits = input.Visits.Where(v => cluster.Select(w => w.Visit).Contains(v.Id)).ToArray(),
                        Santas = new[] {input.Santas[santa]},
                        Days = new [] {input.Days[day]},
                        RouteCosts = input.RouteCosts,
                    };

                    schedulingSovlerVariableBuilders.Add(new SchedulingSolverVariableBuilder(timeslice, schedulingOptimisationInput));
                }
            }

            var schedulingInputVariables = schedulingSovlerVariableBuilders
                .Where(vb => vb.Visits != null && vb.Visits.Count > 1)
                .Select(vb => vb.Build());
                

            var routeResults = schedulingInputVariables
                .AsParallel()
                .Select(schedulingInputVariable => Starter.Optimise(schedulingInputVariable, TargetBuilderType.Default, 0, timelimit))
                .ToList();


            // Construct new output elem

            var optimizationResult = new OptimizationResult()
            {
                OptimisationInput = input,
                Routes = routeResults.Select(r => new Route
                {
                    SantaId = r.SantaIds[0],
                    Waypoints = r.Waypoints[0,0].Select(origWp => new Waypoint
                    {
                        VisitId = origWp.Visit,
                        StartTime = origWp.StartTime
                    }).ToArray(),
                   
                }).ToArray(),
            };


            // assign elapsed in the end.
            sw.Stop();
            optimizationResult.TimeElapsed = (int) (sw.ElapsedMilliseconds / 1000);
            return optimizationResult;
        }
    }
}
