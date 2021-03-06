﻿using System;
using System.Collections.Generic;

namespace Dinmore.Uwp.Models
{
    public class DetectionState
    {
        public ApiRequestParameters ApiRequestParameters { get; internal set; }

        public List<Face> FacesFoundByApi { get; internal set; }

        /// <summary>
        /// Tracks the last time we asked the API anything so we don't get too chatty.
        /// </summary>
        public DateTimeOffset LastImageApiPush { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Holds the current state machine value for the detection of faces.
        /// </summary>
        public DetectionStates State { get; set; }


        /// <summary>
        /// Are faces still present on the video? If not after an amount elasped time
        ///   we will stop the audio
        /// </summary>
        public bool FacesStillPresent { get; set; }

        public DateTimeOffset TimeVideoWasStopped { get; set; }

    }
}
