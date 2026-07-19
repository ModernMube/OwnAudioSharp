//! ASIO backend implementation.
//!
//! ASIO is available on Windows with the `asio` feature.
//! See the project README for setup instructions.
//!
//! # One buffer set per driver
//!
//! Unlike every other cpal backend, an ASIO driver has a *single* set of buffers,
//! allocated once by `ASIOCreateBuffers`. Opening several cpal streams on the same
//! ASIO device does not allocate several buffer sets — the later streams are handed
//! the buffers the first one created.
//!
//! The practical consequences for callers:
//!
//! - The **channel count** and **buffer size** are fixed by the first stream opened on
//!   a given driver. A later stream requesting a different channel count or a
//!   different [`BufferSize::Fixed`](crate::BufferSize::Fixed) is rejected with
//!   [`ErrorKind::UnsupportedConfig`](crate::ErrorKind::UnsupportedConfig) rather than
//!   silently given mismatched buffers.
//! - Opening an input stream when an output stream already exists (or vice versa)
//!   *recreates* the driver's buffers, since ASIO requires input and output buffers to
//!   be created in a single call. Build all streams for a device before starting
//!   playback where possible.
//! - Sharing one buffer set means the output callbacks of several streams mix into the
//!   same driver buffer; the first callback of each buffer period silences it.
//!
//! To run two genuinely independent configurations you need two ASIO devices, or a
//! single stream that carries all the channels you need.

extern crate asio_sys as sys;

use std::{
    sync::{Arc, OnceLock},
    time::Duration,
};

pub use self::{
    device::{Device, Devices, SupportedInputConfigs, SupportedOutputConfigs},
    stream::Stream,
};
use crate::{
    host::com,
    traits::{DeviceTrait, HostTrait, StreamTrait},
    Data, DeviceDescription, DeviceId, Error, FrameCount, InputCallbackInfo, OutputCallbackInfo,
    SampleFormat, StreamConfig, StreamInstant, SupportedStreamConfig,
};

mod device;
mod stream;

/// Global ASIO instance shared across all Host instances.
///
/// ASIO only supports loading a single driver at a time globally, so all Host instances
/// must share the same underlying sys::Asio wrapper to properly coordinate driver access.
static GLOBAL_ASIO: OnceLock<Arc<sys::Asio>> = OnceLock::new();

/// The host for ASIO.
#[derive(Debug)]
pub struct Host {
    asio: Arc<sys::Asio>,
}

impl Host {
    pub fn new() -> Result<Self, Error> {
        com::com_initialized();
        let asio = GLOBAL_ASIO
            .get_or_init(|| Arc::new(sys::Asio::new()))
            .clone();
        let host = Host { asio };
        Ok(host)
    }
}

impl HostTrait for Host {
    type Devices = Devices;
    type Device = Device;

    fn is_available() -> bool {
        true
        //unimplemented!("check how to do this using asio-sys")
    }

    fn devices(&self) -> Result<Self::Devices, Error> {
        Devices::new(self.asio.clone())
    }

    fn default_input_device(&self) -> Option<Self::Device> {
        // ASIO has no concept of a default device, so just use the first.
        self.input_devices().ok().and_then(|mut ds| ds.next())
    }

    fn default_output_device(&self) -> Option<Self::Device> {
        // ASIO has no concept of a default device, so just use the first.
        self.output_devices().ok().and_then(|mut ds| ds.next())
    }
}

impl DeviceTrait for Device {
    type SupportedInputConfigs = SupportedInputConfigs;
    type SupportedOutputConfigs = SupportedOutputConfigs;
    type Stream = Stream;

    fn description(&self) -> Result<DeviceDescription, Error> {
        Device::description(self)
    }

    fn id(&self) -> Result<DeviceId, Error> {
        Device::id(self)
    }

    fn supported_input_configs(&self) -> Result<Self::SupportedInputConfigs, Error> {
        Device::supported_input_configs(self)
    }

    fn supported_output_configs(&self) -> Result<Self::SupportedOutputConfigs, Error> {
        Device::supported_output_configs(self)
    }

    fn default_input_config(&self) -> Result<SupportedStreamConfig, Error> {
        Device::default_input_config(self)
    }

    fn default_output_config(&self) -> Result<SupportedStreamConfig, Error> {
        Device::default_output_config(self)
    }

    fn build_input_stream_raw<D, E>(
        &self,
        config: StreamConfig,
        sample_format: SampleFormat,
        data_callback: D,
        error_callback: E,
        timeout: Option<Duration>,
    ) -> Result<Self::Stream, Error>
    where
        D: FnMut(&Data, &InputCallbackInfo) + Send + 'static,
        E: FnMut(Error) + Send + 'static,
    {
        Device::build_input_stream_raw(
            self,
            config,
            sample_format,
            data_callback,
            error_callback,
            timeout,
        )
    }

    fn build_output_stream_raw<D, E>(
        &self,
        config: StreamConfig,
        sample_format: SampleFormat,
        data_callback: D,
        error_callback: E,
        timeout: Option<Duration>,
    ) -> Result<Self::Stream, Error>
    where
        D: FnMut(&mut Data, &OutputCallbackInfo) + Send + 'static,
        E: FnMut(Error) + Send + 'static,
    {
        Device::build_output_stream_raw(
            self,
            config,
            sample_format,
            data_callback,
            error_callback,
            timeout,
        )
    }
}

impl StreamTrait for Stream {
    fn play(&self) -> Result<(), Error> {
        Stream::play(self)
    }

    fn pause(&self) -> Result<(), Error> {
        Stream::pause(self)
    }

    fn now(&self) -> StreamInstant {
        Stream::now(self)
    }

    fn buffer_size(&self) -> Result<FrameCount, Error> {
        Stream::buffer_size(self)
    }
}
