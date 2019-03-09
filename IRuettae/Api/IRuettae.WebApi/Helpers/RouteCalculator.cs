﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IRuettae.Converter;
using IRuettae.Core;
using IRuettae.Core.Models;
using IRuettae.Persistence.Entities;
using IRuettae.WebApi.Persistence;
using Newtonsoft.Json;
using Santa = IRuettae.Persistence.Entities.Santa;
using Visit = IRuettae.Persistence.Entities.Visit;

namespace IRuettae.WebApi.Helpers
{
    public class RouteCalculator
    {
        public static ConcurrentBag<BackgroundWorker> BackgroundWorkers = new ConcurrentBag<BackgroundWorker>();

        private readonly long routeCalculationId;

        private BackgroundWorker bgWorker;

        public RouteCalculator(RouteCalculation routeCalculation)
        {
            routeCalculationId = routeCalculation.Id;
            SetupBgWorker();
        }

        private void SetupBgWorker()
        {
            bgWorker = new BackgroundWorker();
            BackgroundWorkers.Add(bgWorker);
            bgWorker.Disposed += (sender, args) =>
            {
                var dbSession = SessionFactory.Instance.OpenSession();
                var routeCalculation = dbSession.Get<RouteCalculation>(routeCalculationId);
                if (string.IsNullOrEmpty(routeCalculation.Result))
                {
                    routeCalculation.State = RouteCalculationState.Cancelled;
                    routeCalculation.StateText.Append(new RouteCalculationLog
                    {
                        Log = $"{Environment.NewLine} {DateTime.Now} Background worker stopped"
                    });
                }

                dbSession.Update(routeCalculation);
                dbSession.Flush();
            };

            bgWorker.DoWork += BackgroundWorkerDoWork;
        }

        private void BackgroundWorkerDoWork(object sender, DoWorkEventArgs args)
        {
            try
            {
                #region Prepare

                var dbSession = SessionFactory.Instance.OpenSession();
                var routeCalculation = dbSession.Get<RouteCalculation>(routeCalculationId);

                var santas = dbSession.Query<Santa>().OrderBy(s => s.Id).ToList();

                var visits = dbSession.Query<Visit>()
                    .Where(v => v.Year == routeCalculation.Year && v.Id != routeCalculation.StarterVisitId)
                    .OrderBy(v => v.Id)
                    .ToList();

                visits.ForEach(v =>
                    v.Duration = 60 * (v.NumberOfChildren * routeCalculation.TimePerChildMinutes +
                                       routeCalculation.TimePerChildOffsetMinutes));

                var startVisit = dbSession.Query<Visit>().First(v => v.Id == routeCalculation.StarterVisitId);

                routeCalculation.NumberOfSantas = santas.Count;
                routeCalculation.NumberOfVisits = visits.Count;

                var converter = new PersistenceToCoreConverter();
                var optimizationInput = converter.Convert(routeCalculation.Days, startVisit, visits, santas);

                // remove unnecessary santas
                {
                    // maximum number of additional santas to reach the theoretical optimum
                    var maxAdditional = optimizationInput.Visits.Count(v => !v.IsBreak) - optimizationInput.NumberOfSantas();
                    maxAdditional = Math.Max(0, maxAdditional);
                    routeCalculation.MaxNumberOfAdditionalSantas = Math.Min(routeCalculation.MaxNumberOfAdditionalSantas, maxAdditional);
                }

                routeCalculation.State = RouteCalculationState.Ready;

                var solverConfigs = SolverConfigFactory.CreateSolverConfig(routeCalculation, optimizationInput);
                routeCalculation.AlgorithmData = JsonConvert.SerializeObject(solverConfigs);
                dbSession.Update(routeCalculation);
                dbSession.Flush();
                #endregion Prepare

                #region Run
                var optimizationResults = new OptimizationResult[solverConfigs.Length];

                routeCalculation.StartTime = DateTime.Now;
                for (var i = 0; i < solverConfigs.Length; i++)
                {
                    var solverConfig = solverConfigs[0];
                    var solver = SolverFactory.CreateSolver(optimizationInput, solverConfig);

                    // note: Progress<> is not suitable here as it may use multiple threads
                    var consoleProgress = new EventHandler<string>(OnConsoleProgressOnProgressChanged);
                    var i1 = i; // for lambda
                    var progress = new EventHandler<ProgressReport>((s, report) =>
                    {
                        var realProgress = report.Progress / solverConfigs.Length;
                        realProgress += (double)i1/solverConfigs.Length * 1;
                        report.Progress = realProgress;
                        OnProgressOnProgressChanged(s, report);
                    });

                    routeCalculation.State = RouteCalculationState.Running;
                    dbSession.Update(routeCalculation);
                    dbSession.Flush();

                    optimizationResults[i] = solver.Solve(routeCalculation.TimeLimitMiliseconds / solverConfigs.Length,
                        progress, consoleProgress);
                }

                // compare optimization results and take best
                OptimizationResult optimizationResult = null;
                foreach (var result in optimizationResults)
                {
                    if (optimizationResult == null || result.Cost() < optimizationResult.Cost())
                    {
                        optimizationResult = result;
                    }
                }

                // refresh session as changes where made in other sessions (progress)
                dbSession.Clear();
                routeCalculation = dbSession.Get<RouteCalculation>(routeCalculation.Id);
                var routeCalculationResult = new RouteCalculationResult
                {
                    OptimizationResult = optimizationResult,
                    VisitMap = converter.VisitMap,
                    SantaMap = converter.SantaMap,
                    ZeroTime = converter.ZeroTime
                };
                routeCalculation.Result = JsonConvert.SerializeObject(routeCalculationResult);
                routeCalculation.State = RouteCalculationState.Finished;

                #endregion Run

                routeCalculation.EndTime = DateTime.Now;
                dbSession.Update(routeCalculation);
                dbSession.Flush();
            }
            catch (Exception e)
            {
                var dbSession = SessionFactory.Instance.OpenSession();
                var routeCalculation = dbSession.Get<RouteCalculation>(routeCalculationId);
                routeCalculation.State = RouteCalculationState.Cancelled;
                routeCalculation.StateText.Add(new RouteCalculationLog { Log = $"Error: {e.Message}" });
                dbSession.Update(routeCalculation);
                dbSession.Flush();
            }
        }

        public void StartWorker()
        {
            bgWorker.RunWorkerAsync();
        }

        private void OnConsoleProgressOnProgressChanged(object s, string message)
        {
            try
            {
                var dbSession = SessionFactory.Instance.OpenSession();
                var routeCalculation = dbSession.Get<RouteCalculation>(routeCalculationId);
                routeCalculation.StateText.Add(new RouteCalculationLog { Log = message });
                dbSession.Update(routeCalculation);
                dbSession.Flush();
            }
            catch
            {
                // ignored
            }
        }

        private void OnProgressOnProgressChanged(object s, ProgressReport report)
        {
            try
            {
                var dbSession = SessionFactory.Instance.OpenSession();
                var routeCalculation = dbSession.Get<RouteCalculation>(routeCalculationId);
                routeCalculation.Progress = report.Progress;
                dbSession.Update(routeCalculation);
                dbSession.Flush();

                OnConsoleProgressOnProgressChanged(s, $"Progress: {report.Progress:P2}");
            }
            catch
            {
                // ignored
            }
        }
    }
}