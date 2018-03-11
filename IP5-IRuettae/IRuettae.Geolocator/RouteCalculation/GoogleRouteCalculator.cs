﻿using System.Net;
using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json.Linq;

namespace IRuettae.GeoCalculations.RouteCalculation
{
    public class GoogleRouteCalculator : IRouteCalculator
    {
        private const string BaseUrl = @"https://maps.googleapis.com/maps/api/directions/json?";

        private readonly string apiKey;

        /// <summary>
        /// Implementation of RouteCalculator Interface using the Google API
        /// </summary>
        /// <param name="apiKey">Your Google API Key to use Directions API</param>
        public GoogleRouteCalculator(string apiKey)
        {
            this.apiKey = apiKey;
        }

        public (double distance, double duration) CalculateWalkingDistance(string from, string to)
        {
            return CalculateDistance(from, to, "walking");
        }

        public (double distance, double duration) CalculateWalkingDistance(double fromLat, double fromLng, double toLat, double toLng)
        {
            return CalculateWalkingDistance($"{fromLat},{fromLng}", $"{toLat},{toLng}");
        }

        private (double distance, double duration) CalculateDistance(string from, string to, string mode)
        {
            var units = "metric";
            var callUrl = $"{BaseUrl}origin={from}&destination={to}&mode={mode}&units={units}&key={apiKey}";
            var retJson = new WebClient().DownloadString(callUrl);
            
            dynamic retData = JObject.Parse(retJson);

            try
            {
                double distance = retData.routes[0].legs[0].distance.value;
                double duration = retData.routes[0].legs[0].duration.adafadfvalue;

                return (distance, duration);
            }
            catch (RuntimeBinderException)
            {
                // if binding failed for dynamic data (double => null)
                throw new RouteNotFoundException();
            }
        }

    }
}
