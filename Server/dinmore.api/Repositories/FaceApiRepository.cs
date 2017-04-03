﻿using dinmore.api.Interfaces;
using dinmore.api.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace dinmore.api.Repositories
{
    public class FaceApiRepository : IFaceApiRepository
    {
        private readonly AppSettings _appSettings;

        public FaceApiRepository(IOptions<AppSettings> appSettings)
        {
            _appSettings = appSettings.Value;
        }

        public async Task<IEnumerable<Face>> DetectFaces(byte[] image, bool returnFaceLandmarks, string returnFaceAttributes)
        {
            //call face api
            var responseString = string.Empty;
            using (var httpClient = new HttpClient())
            {

                //setup HttpClient with content
                httpClient.BaseAddress = new Uri(_appSettings.FaceApiDetectBaseUrl);
                httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _appSettings.FaceApiKey);
                var content = new StreamContent(new MemoryStream(image));
                content.Headers.ContentType = new MediaTypeWithQualityHeaderValue("application/octet-stream");

                //construct full API endpoint uri
                var parameters = new Dictionary<string, string> {
                    { "returnFaceId", "true"},
                    { "returnFaceLandmarks", returnFaceLandmarks.ToString() },
                    { "returnFaceAttributes", returnFaceAttributes },
                };
                var apiUri = QueryHelpers.AddQueryString(_appSettings.FaceApiDetectBaseUrl, parameters);
                
                //make request
                var responseMessage = await httpClient.PostAsync(apiUri, content);

                //read response as a json string
                responseString = await responseMessage.Content.ReadAsStringAsync();
            }

            //create emotion scores object. parse json string to object and enumerate
            var responseArray = JArray.Parse(responseString);
            var faces = new List<Face>();
            foreach (var faceResponse in responseArray)
            {
                //deserialise json to face
                var face = JsonConvert.DeserializeObject<Face>(faceResponse.ToString());

                //add face to faces list
                faces.Add(face);
            }

            return faces;
        }


        public async Task<string> AddFaceToFaceList(byte[] image, string faceListId, string targetFace, string userData)
        {
            //call face api
            var responseString = string.Empty;
            using (var httpClient = new HttpClient())
            {
                //setup HttpClient with content
                var apiUrlBase = _appSettings.FaceApiFaceListsPersistedFacesBaseUrl.Replace("[FaceListId]", faceListId);
                httpClient.BaseAddress = new Uri(apiUrlBase);
                httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _appSettings.FaceApiKey);
                var content = new StreamContent(new MemoryStream(image));
                content.Headers.ContentType = new MediaTypeWithQualityHeaderValue("application/octet-stream");

                //construct full API endpoint uri
                var parameters = new Dictionary<string, string> {
                    { "targetFace", targetFace},
                    { "userData", userData }
                };
                var apiUri = QueryHelpers.AddQueryString(apiUrlBase, parameters);

                //make request
                var responseMessage = await httpClient.PostAsync(apiUri, content);

                //read response as a json string
                responseString = await responseMessage.Content.ReadAsStringAsync();

                //parse to dynamic object and extract persisited face id
                dynamic d = JObject.Parse(responseString);
                responseString = d.persistedFaceId;
            }

            return responseString;
        }
    }
}