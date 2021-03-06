﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using IRuettae.Core.ILP.Algorithm;
using IRuettae.Core.ILP.Algorithm.Clustering;
using IRuettae.Core.ILP.Algorithm.Models;
using IRuettae.WebApi.Helpers;
using SolverInputData = IRuettae.Core.ILP.Algorithm.Clustering.SolverInputData;

namespace IRuettae.ConsoleApp
{
    public class TestILPClustering
    {
        private const ConsoleColor InfoColor = ConsoleColor.Cyan;
        private const ConsoleColor ResultColor = ConsoleColor.Green;
        public static void Test()
        {
            var eventTextWriter = new EventTextWriter();
            eventTextWriter.CharWritten += (sender, c) => { Debug.Write(c); };
            Console.OpenStandardOutput();
            Console.SetOut(eventTextWriter);


            ExportMPSVisits(35);
            TestAlgorithm(35);
        }

        private static void TestAlgorithm(int n_visits)
        {
            ConsoleExt.WriteLine($"Start testing algorithm with {n_visits} visits", InfoColor);

            TestSerailDataVisits($"SerializedObjects/ClusteringSolverInput{n_visits}Visits.serial", numberOfRuns: 1);
        }

        private static void ExportMPSVisits(int n_visits)
        {
            var solverInputData = Deserialize($"SerializedObjects/ClusteringSolverInput{n_visits}Visits.serial");

            new ClusteringILPSolver(solverInputData).ExportMPSAsFile($"New_{n_visits}_mps.mps");

            ConsoleExt.WriteLine($"Saved mps for {n_visits} visits", InfoColor);
        }
        private static void TestSerailDataVisits(string serialDataName, int numberOfRuns = 5)
        {
            var solverInputData = Deserialize(serialDataName);
            // solverInputData.DayDuration = solverInputData.DayDuration.Select(d => (int)(d / 0.7)).ToArray();
            double mip_gap = 0;

            for (int i = 1; i <= numberOfRuns; i++)
            {
                var sw = Stopwatch.StartNew();
                var solver = new ClusteringILPSolver(solverInputData);
                solver.Solve(mip_gap, 10 * 60 * 1000);
                var routeResult = solver.GetResult();
                sw.Stop();
                ConsoleExt.WriteLine($"{i}/{numberOfRuns}: Elapsed s: {sw.ElapsedMilliseconds / 1000}", InfoColor);

                Console.WriteLine();
                Console.WriteLine();
                var routes = routeResult.Waypoints
                                .Cast<List<Waypoint>>()
                                .Select(wp => wp.Aggregate("",
                                    (carry, n) => carry + Environment.NewLine + solverInputData.VisitNames[n.Visit]));


                int ctr = 0;
                foreach (var route in routes)
                {
                    File.WriteAllText($"R{ctr}_{mip_gap}.csv", $"Address{Environment.NewLine}{route}");
                    ConsoleExt.WriteLine(ctr.ToString(), ResultColor);
                    ConsoleExt.WriteLine(route, ResultColor);
                    ctr++;
                }

            }
        }

        private static SolverInputData Deserialize(string path)
        {
            using (var stream = File.Open(path, FileMode.Open))
            {
                return (SolverInputData)new BinaryFormatter().Deserialize(stream);
            }
        }

        private void TestFakeData()
        {
            const int numberOfDays = 1;
            const int numberOfSantas = 2;
            const int numberOfVisits = 5;
            var santas = new bool[numberOfDays, numberOfSantas]
            {
                { true, true },
                // { true, true }
            };

            var visitsDuration = new int[numberOfVisits] { 0, 3, 3, 3, 3 };

            var t = VisitState.Default;
            var visits = new VisitState[numberOfDays, numberOfVisits]
            {
                { t, t, t, t, t},
                //   { t, t, t, t, t},
            };


            var distances = new int[numberOfVisits, numberOfVisits]
            {
                {0, 2, 2, 2, 2 },
                {2, 0, 1, 3, 4 },
                {2, 1, 0, 2, 3 },
                {2, 3, 2, 0, 1 },
                {2, 4, 3, 1, 0 },
            };
            var dayDuration = new int[numberOfDays]
            {
                10,
                //10
            };

            var santaBreaks = new int[numberOfSantas][]
            {
                new int[] {},
                new int[] {}
            };

            var solverInputData = new SolverInputData(santas, visitsDuration, visits, distances, dayDuration, santaBreaks);
            var solver = new ClusteringILPSolver(solverInputData);
            solver.Solve(0, 60 * 1000);
        }
    }
}
