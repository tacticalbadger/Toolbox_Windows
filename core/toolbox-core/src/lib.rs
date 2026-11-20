//! Toolbox core: the connection engine behind the Windows app.
//!
//! Everything exposed to C# goes through UniFFI. The app owns all rendering
//! and windowing (and RDP, through Microsoft's own client); this crate owns
//! SSH, SFTP, FTP, VNC, storage and secrets.

uniffi::setup_scaffolding!();

pub mod ad;
pub mod display;
pub mod error;
pub mod ftp;
#[cfg(feature = "test-support")]
pub mod ftp_test_server;
pub mod onepassword;
pub mod runtime;
pub mod sftp;
pub mod ssh;
#[cfg(feature = "test-support")]
pub mod ssh_test_server;
pub mod store;
pub mod vault;

pub use display::*;
pub use error::CoreError;
pub use onepassword::ResolvedCredential;
pub use sftp::*;
pub use ssh::*;
pub use store::*;
pub use vault::*;

/// Version of the core library, surfaced in the app's About box.
#[uniffi::export]
pub fn core_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}
