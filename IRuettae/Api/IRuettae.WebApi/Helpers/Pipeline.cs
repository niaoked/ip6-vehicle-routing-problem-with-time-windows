﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Entity;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Helpers;
using System.Web.Hosting;
using IRuettae.Core.ILP;
using IRuettae.Core.ILP.Algorithm;
using IRuettae.Core.ILP.Algorithm.Models;
using IRuettae.Core.Models;
using IRuettae.Persistence.Entities;
using IRuettae.Preprocessing.Mapping;
using IRuettae.WebApi.Models;
using IRuettae.WebApi.Persistence;
using Newtonsoft.Json;
using NHibernate;
using Santa = IRuettae.Persistence.Entities.Santa;
using Visit = IRuettae.Persistence.Entities.Visit;
using Waypoint = IRuettae.Core.ILP.Algorithm.Waypoint;

namespace IRuettae.WebApi.Helpers
{
    public class Pipeline
    {
        public static ConcurrentBag<BackgroundWorker> BackgroundWorkers = new ConcurrentBag<BackgroundWorker>();

        private RouteCalculation routeCalculation;
        private readonly ISession dbSession;
        private BackgroundWorker bgWorker;


        public Pipeline(RouteCalculation routeCalculation)
        {
            this.routeCalculation = routeCalculation;
            routeCalculation.StateText = "";
            this.dbSession = SessionFactory.Instance.OpenSession();
            dbSession.FlushMode = FlushMode.Always;

            SetupBgWorker();

        }

        private void SetupBgWorker()
        {
            bgWorker = new BackgroundWorker();
            BackgroundWorkers.Add(bgWorker);
            bgWorker.Disposed += (sender, args) =>
            {
                if (string.IsNullOrEmpty(routeCalculation.Result))
                {
                    routeCalculation.State = RouteCalculationState.Cancelled;
                    routeCalculation.StateText += $"{Environment.NewLine} {DateTime.Now} Background worker stopped";
                }

                dbSession.Update(routeCalculation);
                dbSession.Flush();
            };

            routeCalculation = dbSession.Merge(routeCalculation);
            bgWorker.DoWork += BackgroundWorkerDoWork;
        }

        private void BackgroundWorkerDoWork(object sender, DoWorkEventArgs args)
        {
            try
            {
                #region Prepare

                var santas = dbSession.Query<Santa>().ToList();

                var visits = dbSession.Query<Visit>()
                    .Where(v => v.Year == routeCalculation.Year && v.Id != routeCalculation.StarterVisitId)
                    .ToList();

                visits.ForEach(v => v.Duration = 60 * (v.NumberOfChildren * routeCalculation.TimePerChild + routeCalculation.TimePerChildOffset));

                var startVisit = dbSession.Query<Visit>().First(v => v.Id == routeCalculation.StarterVisitId);

                routeCalculation.NumberOfSantas = santas.Count;
                routeCalculation.NumberOfVisits = visits.Count;

                var ilpData = JsonConvert.DeserializeObject<ILPStarterData>(routeCalculation.AlgorithmData);

                var converter = new Converter.PersistenceToCoreConverter();
                var optimizationInput = converter.Convert(routeCalculation.Days, startVisit, visits, santas);

                routeCalculation.State = RouteCalculationState.Ready;
                dbSession.Update(routeCalculation);
                dbSession.Flush();

                #endregion Prepare

                routeCalculation.StartTime = DateTime.Now;

                routeCalculation.State = RouteCalculationState.Running;
                dbSession.Update(routeCalculation);
                dbSession.Flush();


                #region Clustering

                var clusteringSolverVariableBuilder = new ClusteringSolverVariableBuilder(optimizationInput, ilpData.TimeSliceDuration);
                var clusteringSolverInputData = clusteringSolverVariableBuilder.Build();



#if DEBUG
                var serialPath = HostingEnvironment.MapPath($"~/App_Data/Clustering{routeCalculation.Id}_{ilpData.ClusteringOptimizationFunction}_{routeCalculation.NumberOfVisits}.serial");
                if (serialPath != null)
                {
                    using (var stream = File.Open(serialPath, FileMode.Create))
                    {
                        new BinaryFormatter().Serialize(stream, clusteringSolverInputData);
                    }
                }
                var mpsPath = HostingEnvironment.MapPath($"~/App_Data/Clustering_{routeCalculation.Id}_{ilpData.ClusteringOptimizationFunction}_{routeCalculation.NumberOfVisits}.mps");
                Starter.SaveMps(mpsPath, clusteringSolverInputData, ilpData.ClusteringOptimizationFunction);
#endif

                var phase1Result = Starter.Optimise(clusteringSolverInputData, ilpData.ClusteringOptimizationFunction, ilpData.ClusteringMipGap, ilpData.ClusteringTimeLimit);
                if (phase1Result == null)
                {
                    throw new Exception("Clustering couldn't be solved");
                }
                routeCalculation.StateText += $"{DateTime.Now}: Clustering done{Environment.NewLine}";

                var clusteredRoutesSb = new StringBuilder();
                for (int santa = 0; santa < phase1Result.Waypoints.GetLength(0); santa++)
                {
                    for (int day = 0; day < phase1Result.Waypoints.GetLength(1); day++)
                    {
                        var wp = phase1Result.Waypoints[santa, day].Aggregate(string.Empty, (carry, n) => carry + Environment.NewLine + $"[{n.RealVisitId} {clusteringSolverInputData.VisitNames[n.Visit]}]");
                        clusteredRoutesSb.Append($"Route Santa {santas[santa].Name} on {phase1Result.StartingTime[day]}");
                        clusteredRoutesSb.AppendLine(wp);
                        clusteredRoutesSb.AppendLine(new string('-', 20));
                    }
                }

                ilpData.ClusteringResult = clusteredRoutesSb.ToString();
                routeCalculation.AlgorithmData = JsonConvert.SerializeObject(ilpData);

                dbSession.Update(routeCalculation);
                dbSession.Flush();

                #endregion Clustering


                #region Scheduling

                var schedulingSovlerVariableBuilders = new List<SchedulingSolverVariableBuilder>();
                foreach (var santa in Enumerable.Range(0, phase1Result.Waypoints.GetLength(0)))
                {
                    foreach (var day in Enumerable.Range(0, phase1Result.Waypoints.GetLength(1)))
                    {
                        var cluster = phase1Result.Waypoints[santa, day];
                        var schedulingOptimizationInput = new OptimizationInput
                        {
                            Visits = optimizationInput.Visits.Where(v => cluster.Select(w => w.Visit).Contains(v.Id)).ToArray(),
                            Santas = new[] { optimizationInput.Santas[santa] },
                            Days = new[] { optimizationInput.Days[day] },
                            RouteCosts = optimizationInput.RouteCosts,
                        };

                        schedulingSovlerVariableBuilders.Add(new SchedulingSolverVariableBuilder(ilpData.TimeSliceDuration, schedulingOptimizationInput));
                    }
                }

                var schedulingInputVariables = schedulingSovlerVariableBuilders
                    .Where(vb => vb.Visits != null && vb.Visits.Count > 1)
                    .Select(vb => vb.Build());

#if DEBUG
                // Must be accessed thread safe
                long counter = 0;
#endif

                var routeResults = schedulingInputVariables
                    .AsParallel()
                    .Select(schedulingInputVariable =>
                    {
#if DEBUG
                        var mpsPathScheduling = HostingEnvironment.MapPath($"~/App_Data/Scheduling_{routeCalculation.Id}_{Interlocked.Increment(ref counter)}_{Guid.NewGuid().ToString()}.mps");
                        Starter.SaveMps(mpsPathScheduling, schedulingInputVariable, TargetBuilderType.Default);
#endif
                        return Starter.Optimise(schedulingInputVariable, TargetBuilderType.Default, ilpData.SchedulingMipGap, ilpData.SchedulingTimeLimit);
                    })
                    .ToList();

                ilpData.SchedulingResult = JsonConvert.SerializeObject(routeResults);
                routeCalculation.AlgorithmData = JsonConvert.SerializeObject(ilpData);
                routeCalculation.StateText += $"{DateTime.Now}: Scheduling done{Environment.NewLine}";
                dbSession.Update(routeCalculation);
                dbSession.Flush();

                #endregion Scheduling

                // Construct new output elem
                var optimizationResult = new OptimizationResult()
                {
                    OptimizationInput = optimizationInput,
                    Routes = routeResults.Select(r => new Core.Models.Route
                    {
                        SantaId = r.SantaIds[0],
                        Waypoints = r.Waypoints[0, 0].Select(origWp => new Core.Models.Waypoint
                        {
                            VisitId = origWp.Visit,
                            StartTime = origWp.StartTime
                        }).ToArray(),

                    }).ToArray(),
                };


                routeCalculation.Result = JsonConvert.SerializeObject(optimizationResult);
                routeCalculation.State = RouteCalculationState.Finished;

                // Todo: metrics

                routeCalculation.EndTime = DateTime.Now;
                dbSession.Update(routeCalculation);
                dbSession.Flush();

            }
            catch (Exception e)
            {
                routeCalculation.State = RouteCalculationState.Cancelled;
                routeCalculation.StateText += "Error: " + e.Message;
                dbSession.Update(routeCalculation);
                dbSession.Flush();
            }
        }

        /*private void BackgroundWorkerDoWorkOld(object sender, DoWorkEventArgs args)
        {
            try
            {
                var santas = dbSession.Query<Santa>().ToList();

                var visits = dbSession.Query<Visit>()
                    .Where(v => v.Year == routeCalculation.Year && v.Id != routeCalculation.StarterVisitId)
                    .ToList();

                visits.ForEach(v => v.Duration = 60 * (v.NumberOfChildren * routeCalculation.TimePerChild + routeCalculation.TimePerChildOffset));

                var startVisit = dbSession.Query<Visit>().First(v => v.Id == routeCalculation.StarterVisitId);

                routeCalculation.NumberOfSantas = santas.Count;
                routeCalculation.NumberOfVisits = visits.Count;

                routeCalculation.State = RouteCalculationState.Ready;
                dbSession.Update(routeCalculation);
                dbSession.Flush();

                var ilpData = JsonConvert.DeserializeObject<ILPStarterData>(routeCalculation.AlgorithmData);



                // ******************************

                #region Clustering

                // ******************************
                var converter = new Converter.PersistenceToCoreConverter();
                var optimizationInput = converter.Convert(routeCalculation.Days, startVisit, visits, santas);

                var ilpSolver = new ILPSolver(optimizationInput, ilpData.TimeSliceDuration);

                var clusteringSolverInputData = ClusteringSolverVariableBuilder.Build();

                routeCalculation.StartTime = DateTime.Now;

                routeCalculation.State = RouteCalculationState.RunningPhase1;
                dbSession.Update(routeCalculation);
                dbSession.Flush();

                TargetBuilderType targetType = TargetBuilderType.Default;
                switch (ilpData.ClusteringOptimizationFunction)
                {
                    case ClusteringOptimizationGoals.OverallMinTime:
                        targetType = TargetBuilderType.MinTimeOnly;
                        break;
                    case ClusteringOptimizationGoals.MinTimePerSanta:
                        targetType = TargetBuilderType.Default;
                        break;
                    case ClusteringOptimizationGoals.MinAvgTimePerSanta:
                        targetType = TargetBuilderType.MinAvgTimeOnly;
                        break;
                    default:
                        targetType = TargetBuilderType.Default;
                        break;
                }

#if DEBUG
                var serialPath = HostingEnvironment.MapPath($"~/App_Data/Clustering{routeCalculation.Id}_{ilpData.ClusteringOptimizationFunction}_{routeCalculation.NumberOfVisits}.serial");
                if (serialPath != null)
                {
                    using (var stream = File.Open(serialPath, FileMode.Create))
                    {
                        new BinaryFormatter().Serialize(stream, clusteringSolverInputData);
                    }
                }
                var mpsPath = HostingEnvironment.MapPath($"~/App_Data/Clustering_{routeCalculation.Id}_{ilpData.ClusteringOptimizationFunction}_{routeCalculation.NumberOfVisits}.mps");
                Starter.SaveMps(mpsPath, clusteringSolverInputData, targetType);
#endif


                var phase1Result = Starter.Optimise(clusteringSolverInputData, targetType, ilpData.ClusteringMipGap, ilpData.ClusteringTimeLimit);

                routeCalculation.StateText += $"{DateTime.Now}: Clustering done{Environment.NewLine}";


                var clusteredRoutesSb = new StringBuilder();
                for (int santa = 0; santa < phase1Result.Waypoints.GetLength(0); santa++)
                {
                    for (int day = 0; day < phase1Result.Waypoints.GetLength(1); day++)
                    {
                        var wp = phase1Result.Waypoints[santa, day].Aggregate(string.Empty, (carry, n) => carry + Environment.NewLine + $"[{n.RealVisitId} {clusteringSolverInputData.VisitNames[n.Visit]}]");
                        clusteredRoutesSb.Append($"Route Santa {santas[santa].Name} on {phase1Result.StartingTime[day]}");
                        clusteredRoutesSb.AppendLine(wp);
                        clusteredRoutesSb.AppendLine(new string('-', 20));
                    }
                }

                ilpData.ClusteringResult = clusteredRoutesSb.ToString();
                routeCalculation.AlgorithmData = JsonConvert.SerializeObject(ilpData);

                #endregion Clustering

                dbSession.Update(routeCalculation);
                dbSession.Flush();

                // ******************************

                #region Scheduling

                // ******************************

                var schedulingSovlerVariableBuilders = new List<SchedulingSolverVariableBuilder>();
                foreach (var santa in Enumerable.Range(0, phase1Result.Waypoints.GetLength(0)))
                {
                    foreach (var day in Enumerable.Range(0, phase1Result.Waypoints.GetLength(1)))
                    {
                        var cluster = phase1Result.Waypoints[santa, day];
                        schedulingSovlerVariableBuilders.Add(new SchedulingSolverVariableBuilder(ilpData.TimeSliceDuration, new List<Santa> { santas[santa] }, visits.Where(v => cluster.Select(w => w.RealVisitId).Contains(v.Id)).ToList(), new List<(DateTime, DateTime)> { routeCalculation.Days[day] }));
                    }
                }


                routeCalculation.State = RouteCalculationState.RunningPhase2;
                dbSession.Update(routeCalculation);
                dbSession.Flush();
                var inputData = schedulingSovlerVariableBuilders.Where(vb => vb.Visits != null && vb.Visits.Count > 1)
                    .Select(vb => vb.Build())
                    .ToList();


                // TODO: Atomic Int oder so verwenden
                //int ctr = 0;
                var routeResults = inputData.AsParallel()
                    .Select(schedulingInputdata =>
                    {
                        //var mpsPathScheduling = HostingEnvironment.MapPath($"~/App_Data/Scheduling_{routeCalculation.Id}_{ctr++}_{Guid.NewGuid().ToString()}.mps");
                        //Starter.SaveMps(mpsPathScheduling, schedulingInputdata, TargetBuilderType.Default);

                        var retVal = new SchedulingResult
                        {
                            Route = Starter.Optimise(schedulingInputdata, TargetBuilderType.Default, ilpData.SchedulingMipGap, ilpData.SchedulingTimeLimit),
                            StartingTime = schedulingInputdata.DayStartingTimes[0]
                        };
                        if (retVal.Route != null)
                        {
                            retVal.Route.StartingTime = new[] { retVal.StartingTime };
                        }

                        return retVal;
                    })
                    .ToList();

                ilpData.SchedulingResult = JsonConvert.SerializeObject(routeResults);
                routeCalculation.AlgorithmData = JsonConvert.SerializeObject(ilpData);
                // gets captured by eventwriter
                routeCalculation.StateText += $"{DateTime.Now}: Scheduling done{Environment.NewLine}";
                dbSession.Update(routeCalculation);
                dbSession.Flush();

                #endregion Scheduling

                #region metrics

                foreach (var routeResult in routeResults)
                {
                    for (int day = 0; day < routeResult.Route.StartingTime.Length; day++)
                    {
                        for (int santa = 0; santa < routeResult.Route.Waypoints.GetLength(0); santa++)
                        {
                            var waypoints = routeResult.Route.Waypoints[santa, day];
                            foreach (var waypoint in waypoints)
                            {
                                var visit = visits.Where(v => v.Id == waypoint.RealVisitId).First();
                                var visitStart = routeResult.Route.StartingTime[day].AddSeconds(waypoint.StartTime * ilpData.TimeSliceDuration);
                                var visitEnd = visitStart.AddSeconds(visit.Duration);

                                foreach (var desired in visit.Desired)
                                {
                                    // outside
                                    if (visitStart <= desired.Start && visitEnd >= desired.End)
                                    {
                                        routeCalculation.DesiredSeconds += (desired.End - desired.Start).Value.TotalSeconds;
                                    }

                                    // inside
                                    else if (visitStart > desired.Start && visitEnd < desired.End)
                                    {
                                        routeCalculation.DesiredSeconds += (visitEnd - visitStart).TotalSeconds;
                                    }

                                    // right
                                    else if (visitStart > desired.Start && visitEnd >= desired.End && visitStart < desired.End)
                                    {
                                        routeCalculation.DesiredSeconds += (desired.End - visitStart).Value.TotalSeconds;
                                    }

                                    // left
                                    else if (visitStart <= desired.Start && visitEnd < desired.End && visitEnd > desired.Start)
                                    {
                                        routeCalculation.DesiredSeconds += (visitEnd - desired.Start).Value.TotalSeconds;
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (var routeResult in routeResults)
                {
                    for (int day = 0; day < routeResult.Route.StartingTime.Length; day++)
                    {
                        for (int santa = 0; santa < routeResult.Route.Waypoints.GetLength(0); santa++)
                        {
                            var waypoints = routeResult.Route.Waypoints[santa, day];
                            var latestVisit = new DateTime().Add(routeResult.Route.StartingTime[day].AddSeconds(waypoints.Take(waypoints.Count - 1).Max(wp => wp.StartTime) * ilpData.TimeSliceDuration).TimeOfDay);
                            if (latestVisit > routeCalculation.LatestVisit)
                            {
                                routeCalculation.LatestVisit = latestVisit;
                            }
                        }
                    }
                }

                routeCalculation.LongestRouteDistance = routeResults.Max(rr => rr.Route.Waypoints.Cast<List<Waypoint>>()
                    .Max(wpl =>
                    {
                        var totalDistance = 0;
                        var lastwp = wpl.First();
                        foreach (var wp in wpl)
                        {
                            totalDistance += dbSession.Query<Way>()
                                .Single(w => w.From.Id.Equals(lastwp.RealVisitId) && w.To.Id.Equals(wp.RealVisitId))
                                .Distance;
                            lastwp = wp;
                        }

                        return totalDistance;
                    }));

                routeCalculation.LongestRouteTime = routeResults.Max(rr => rr.Route.Waypoints.Cast<List<Waypoint>>()
                    .Sum(wpl =>
                    {
                        var totalDuration = 0;
                        var lastwp = wpl.First();
                        foreach (var wp in wpl)
                        {
                            totalDuration += dbSession.Query<Way>()
                                .Single(w => w.From.Id.Equals(lastwp.RealVisitId) && w.To.Id.Equals(wp.RealVisitId))
                                .Duration;
                            lastwp = wp;
                        }

                        return totalDuration;
                    }));

                routeCalculation.TotalWaytime = routeResults.Sum(rr => rr.Route.Waypoints.Cast<List<Waypoint>>()
                    .Sum(wpl =>
                    {
                        var totalDuration = 0;
                        var lastwp = wpl.First();
                        foreach (var wp in wpl)
                        {
                            totalDuration += dbSession.Query<Way>()
                                .Single(w => w.From.Id.Equals(lastwp.RealVisitId) && w.To.Id.Equals(wp.RealVisitId))
                                .Duration;
                            lastwp = wp;
                        }

                        return totalDuration;
                    }));

                routeCalculation.WaytimePerSanta = routeCalculation.TotalWaytime / routeResults.Sum(rr => rr.Route.Waypoints.Length);
                routeCalculation.TotalVisitTime = visits.Sum(v => v.Duration);


                routeCalculation.LongestDay = routeResults.Max(rr => rr.Route.Waypoints.Cast<List<Waypoint>>().Max(wpl => (wpl.Last().StartTime - wpl.First().StartTime) * ilpData.TimeSliceDuration));

                routeCalculation.NumberOfRoutes = routeResults.Count;

                #endregion metrics


                dbSession.Update(routeCalculation);
                dbSession.Flush();


                routeCalculation.Result = null; // Todo: ilpData.SchedulingResult;
                routeCalculation.State = RouteCalculationState.Finished;

                routeCalculation.EndTime = DateTime.Now;
                dbSession.Update(routeCalculation);
                dbSession.Flush();
            }
            catch (Exception e)
            {
                routeCalculation.State = RouteCalculationState.Cancelled;
                routeCalculation.StateText += "Error: " + e.Message;
                dbSession.Update(routeCalculation);
                dbSession.Flush();
            }
        }*/

        public void StartWorker()
        {
            bgWorker.RunWorkerAsync();
        }
    }
}