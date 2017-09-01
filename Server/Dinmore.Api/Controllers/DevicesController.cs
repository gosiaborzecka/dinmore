using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using dinmore.api.Interfaces;
using Dinmore.Api.Models;

namespace Dinmore.Api.Controllers
{
    [Produces("application/json")]
    [Route("api/Devices")]
    public class DevicesController : Controller
    {
        private readonly IStoreRepository _storeRepository;

        public DevicesController(IStoreRepository storeRepository)
        {
            _storeRepository = storeRepository;
        }

        /// <summary>
        /// Returns an array of all devices in the storage table
        /// </summary>
        /// <returns>An array of all devices in the storage table</returns>
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            //log device data to storage
            var devices = await _storeRepository.GetDevices();

            return Ok(devices);
        }

        /// <summary>
        /// Adds a device to the store for devices
        /// </summary>
        /// <param name="Label">The label for the device in the context of the exhibit. Normally 001, 002, 003 etc</param>
        /// <param name="Exhibit">The label for the exhibit where the device is housed. i.e. ShrewsburyPanoramic</param>
        /// <param name="Venue">The label for venue where the exhibit is housed. i.e. the museum name</param>
        /// <returns>A 200 message continaing the guid for the newly created device</returns>
        [HttpPost]
        public async Task<IActionResult> Post(Device device)
        {
            var deviceId = Guid.NewGuid();
            device.Id = deviceId;

            //log device data to storage
            await _storeRepository.StoreDevice(device);

            return Ok(deviceId);
        }

        //// PUT: api/Default1/5
        //[HttpPut("{id}")]
        //public void Put(int id, [FromBody]string value)
        //{
        //}
        // PUT: api/Devices2/5
        [HttpPut("{id}")]
        public async Task<IActionResult> Put(string id, Device device)
        {
            // Update device data to storage
            await _storeRepository.ReplaceDevice(device);

            return Ok(device);
        }

        /// <summary>
        /// Deletes a device from storage
        /// </summary>
        /// <param name="deviceId">The id (guid) of the device to be deleted</param>
        /// <returns>A 204 message indicating that the device was deleted and there is no content</returns>
        [HttpDelete]
        public async Task<IActionResult> Delete(string deviceId)
        {
            //remove the device from storage
            await _storeRepository.DeleteDevice(deviceId);

            return NoContent();
        }

    }
}