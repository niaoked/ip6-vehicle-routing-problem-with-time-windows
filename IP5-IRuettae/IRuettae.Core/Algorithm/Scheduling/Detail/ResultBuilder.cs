﻿using System;
using System.Diagnostics;
using System.Linq;

namespace IRuettae.Core.Algorithm.Scheduling.Detail
{
    internal class ResultBuilder
    {
        private readonly SolverData solverData;

        public ResultBuilder(SolverData solverData)
        {
            this.solverData = solverData;
        }

        public Route CreateResult()
        {
            var route = new Route(solverData.NumberOfSantas, solverData.NumberOfDays);

            for (int day = 0; day < solverData.NumberOfDays; day++)
            {
                for (int santa = 0; santa < solverData.NumberOfSantas; santa++)
                {
                    Waypoint? nextLocation = new Waypoint(0, -1, solverData.Input.VisitIds[0]);
                    nextLocation = GetNextLocation(day, santa, nextLocation.Value);
                    if (!nextLocation.HasValue)
                    {
                        // no visits that evening
                        continue;
                    }

                    // create way from home
                    {
                        var distance = solverData.Input.Distances[solverData.StartEndPoint, nextLocation.Value.Visit];
                        nextLocation = new Waypoint(0, nextLocation.Value.StartTime - distance - 1, solverData.Input.VisitIds[0]);
                    }

                    do
                    {
                        route.Waypoints[santa, day].Add(nextLocation.Value);
                        nextLocation = GetNextLocation(day, santa, nextLocation.Value);
                    } while (nextLocation.HasValue);

                    // append way back home
                    {
                        var last = route.Waypoints[santa, day].LastOrDefault();
                        var duration = solverData.Input.VisitsDuration[last.Visit];
                        var distance = solverData.Input.Distances[last.Visit, solverData.StartEndPoint];
                        route.Waypoints[santa, day].Add(new Waypoint(0, last.StartTime + duration + distance, solverData.Input.VisitIds[0]));
                    }
                }
            }

            Debug.Write("Result is:");
            Debug.Write(route.ToString());

            return route;
        }

        private Waypoint? GetNextLocation(int day, int santa, Waypoint value)
        {
            int nextPossibleStart = value.StartTime + Math.Max(1, solverData.Input.VisitsDuration[value.Visit]);
            for (int timeslice = nextPossibleStart; timeslice < solverData.SlicesPerDay[day]; timeslice++)
            {
                for (int visit = 1; visit < solverData.NumberOfVisits; visit++)
                {
                    if (visit == value.Visit)
                    {
                        continue;
                    }

                    if (solverData.Variables.VisitsPerSanta[day][santa][visit, timeslice].SolutionValue() == 1)
                    {
                        return new Waypoint(visit, timeslice, solverData.Input.VisitIds[visit]);
                    }
                }
            }
            return null;
        }
    }
}