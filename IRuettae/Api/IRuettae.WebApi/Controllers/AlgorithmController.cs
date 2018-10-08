﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using System.Web.Hosting;
using System.Web.Http;
using IRuettae.Core.ILP.Algorithm;
using IRuettae.Persistence.Entities;
using IRuettae.Preprocessing.Mapping;
using IRuettae.WebApi.Helpers;
using IRuettae.WebApi.Models;
using IRuettae.WebApi.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IRuettae.WebApi.Controllers
{
    [RoutePrefix("api/algorithm")]
    public class AlgorithmController : ApiController
    {
        [HttpPost]
        public Route CalculateRoute([FromBody] AlgorithmStarter algorithmStarter)
        {

            using (var dbSession = SessionFactory.Instance.OpenSession())
            using (var transaction = dbSession.BeginTransaction())
            {
                var visits = dbSession.Query<Visit>().Where(v => v.Year == algorithmStarter.Year).ToList();
                visits.ForEach(v => v.Duration = 60 * (v.NumberOfChildren * algorithmStarter.TimePerChild + algorithmStarter.Beta0));
                visits.Sort((a, b) =>
                {
                    if (a.Id == algorithmStarter.StarterId)
                    {
                        return -1;
                    }
                    if (b.Id == algorithmStarter.StarterId)
                    {
                        return 1;
                    }
                    return a.Id.CompareTo(b.Id);
                });



                var solverVariableBuilder = new SchedulingSolverVariableBuilder(algorithmStarter.TimeSliceDuration)
                {
                    Visits = visits,
                    Santas = dbSession.Query<Santa>().ToList(),
                    Days = algorithmStarter.Days
                };

                var solverInputData = solverVariableBuilder.Build();
                var mpsPathScheduling = HostingEnvironment.MapPath($"~/App_Data/Scheduling_{visits.Count}.mps");
                Starter.SaveMps(mpsPathScheduling, solverInputData, TargetBuilderType.Default);

                return Starter.Optimise(solverInputData);
            }
        }
        /// <summary>
        /// Starts a new route calculation job
        /// </summary>
        /// <param name="algorithmStarter"></param>
        /// <returns>the id of the route calcluation job</returns>
        [HttpPost]
        [Route("StartRouteCalculation")]
        public long StartRouteCalculation([FromBody]AlgorithmStarter algorithmStarter)
        {

            RouteCalculation rc;
            RouteCalculation rc2;
            RouteCalculation rc3;

            using (var dbSession = SessionFactory.Instance.OpenSession())
            {
                rc = new RouteCalculation
                {
                    Days = algorithmStarter.Days,
                    SantaJson = "",
                    VisitsJson = "",
                    StarterVisitId = algorithmStarter.StarterId,
                    State = RouteCalculationState.Creating,
                    TimePerChild = algorithmStarter.TimePerChild,
                    TimePerChildOffset = algorithmStarter.Beta0,
                    TimeSliceDuration = algorithmStarter.TimeSliceDuration,
                    Year = algorithmStarter.Year,
                    ClusteringOptimizationFunction = ClusteringOptimizationGoals.OverallMinTime,
                    ClustringMipGap = Properties.Settings.Default.MIPGapClustering,
                    ClusteringTimeLimit =  Properties.Settings.Default.TimelimitClustering,
                    SchedulingMipGap = Properties.Settings.Default.MIPGapScheduling,
                    SchedulingTimeLimit = Properties.Settings.Default.TimelimitScheduling,
                };
                rc = dbSession.Merge(rc);

                rc2 = new RouteCalculation
                {
                    Days = algorithmStarter.Days,
                    SantaJson = "",
                    VisitsJson = "",
                    StarterVisitId = algorithmStarter.StarterId,
                    State = RouteCalculationState.Creating,
                    TimePerChild = algorithmStarter.TimePerChild,
                    TimePerChildOffset = algorithmStarter.Beta0,
                    TimeSliceDuration = algorithmStarter.TimeSliceDuration,
                    Year = algorithmStarter.Year,
                    ClusteringOptimizationFunction = ClusteringOptimizationGoals.MinTimePerSanta,
                    ClustringMipGap = Properties.Settings.Default.MIPGapClustering,
                    ClusteringTimeLimit = Properties.Settings.Default.TimelimitClustering,
                    SchedulingMipGap = Properties.Settings.Default.MIPGapScheduling,
                    SchedulingTimeLimit = Properties.Settings.Default.TimelimitScheduling,
                };
                rc2 = dbSession.Merge(rc2);
                rc3 = new RouteCalculation
                {
                    Days = algorithmStarter.Days,
                    SantaJson = "",
                    VisitsJson = "",
                    StarterVisitId = algorithmStarter.StarterId,
                    State = RouteCalculationState.Creating,
                    TimePerChild = algorithmStarter.TimePerChild,
                    TimePerChildOffset = algorithmStarter.Beta0,
                    TimeSliceDuration = algorithmStarter.TimeSliceDuration,
                    Year = algorithmStarter.Year,
                    ClusteringOptimizationFunction = ClusteringOptimizationGoals.MinAvgTimePerSanta,
                    ClustringMipGap = Properties.Settings.Default.MIPGapClustering,
                    ClusteringTimeLimit = Properties.Settings.Default.TimelimitClustering,
                    SchedulingMipGap = Properties.Settings.Default.MIPGapScheduling,
                    SchedulingTimeLimit = Properties.Settings.Default.TimelimitScheduling,
                };
                rc3 = dbSession.Merge(rc3);
            }

            Task.Run(() => new Pipeline(rc).StartWorker());
            Task.Run(() => new Pipeline(rc2).StartWorker());
            Task.Run(() => new Pipeline(rc3).StartWorker());

            return rc.Id;




            //var serialPath = HostingEnvironment.MapPath($"~/App_Data/SolverInputNew{n_visits}Visits.serial");
            //using (var stream = File.Open(serialPath, FileMode.Create))
            //{
            //    new BinaryFormatter().Serialize(stream, solverInputData);
            //}

            //var mpsPath = HostingEnvironment.MapPath($"~/App_Data/MPS_{n_visits}Visits_new.mps");
            //Starter.SaveMps(mpsPath, solverInputData);

            //var sw = Stopwatch.StartNew();
            //var routeResult = Starter.Optimise(solverInputData, MIP_GAP: 0.5);
            //sw.Stop();
            //var routes = routeResult.Waypoints
            //    .Cast<List<Waypoint>>()
            //    .Select(wp => wp.Aggregate("",
            //        (carry, n) => carry + Environment.NewLine + solverInputData.VisitNames[n.Visit]))
            //    .ToList();


            //var ctr = 0;
            //foreach (var route in routes)
            //{
            //    File.WriteAllText(HostingEnvironment.MapPath($"~/App_Data/R{ctr}_{0.5}.csv"), $"Address{Environment.NewLine}{route}");
            //    //ConsoleExt.WriteLine(ctr.ToString(), ResultColor);
            //    //ConsoleExt.WriteLine(route, ResultColor);
            //    ctr++;
            //}


            //Debug.WriteLine("Elapsed ms: " + sw.ElapsedMilliseconds);
            //return routes;
        }

        [HttpGet]
        [Route("RouteCalculations")]
        public IEnumerable<RouteCalculation> GetRouteCalculations()
        {
            using (var dbSession = SessionFactory.Instance.OpenSession())
            {
                if (Pipeline.BackgroundWorkers.IsEmpty)
                {
                    dbSession.Query<RouteCalculation>()
                        .Where(rc => new[] { RouteCalculationState.Ready, RouteCalculationState.RunningPhase1, RouteCalculationState.RunningPhase2, RouteCalculationState.RunningPhase3 }.Contains(rc.State))
                        .ToList()
                        .ForEach(rc =>
                        {
                            rc.State = RouteCalculationState.Cancelled;
                            dbSession.Update(rc);
                        });
                    dbSession.Flush();
                }

                var routeCalculations = dbSession.Query<RouteCalculation>().ToList();
                return routeCalculations;
            }
        }


        [HttpGet]
        [Route("RouteCalculationWaypoints")]
        public IEnumerable<object> RouteCalculationWaypoints(long id)
        {
            using (var dbSession = SessionFactory.Instance.OpenSession())
            {
                var routeCalculation = dbSession.Get<RouteCalculation>(id);
                var schedulingResults = JsonConvert.DeserializeObject<SchedulingResult[]>(routeCalculation.Result);

                return schedulingResults.Select(sr => sr.Route.Waypoints[0, 0].Select(wp => new
                {
                    Visit = (VisitDTO)dbSession.Get<Visit>(wp.RealVisitId),
                    VisitStartTime = sr.StartingTime.AddSeconds(wp.StartTime * routeCalculation.TimeSliceDuration),
                    VisitEndtime = sr.StartingTime.AddSeconds(wp.StartTime * routeCalculation.TimeSliceDuration).AddSeconds(dbSession.Get<Visit>(wp.RealVisitId).Duration),
                    SantaName = dbSession.Get<Santa>(sr.Route.SantaIds[0])?.Name,
                }).ToList()).ToList();
            }
        }
    }
}
