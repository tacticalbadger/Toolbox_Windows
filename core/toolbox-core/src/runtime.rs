//! One shared Tokio runtime for every session the app opens.

use std::sync::LazyLock;

use tokio::runtime::Runtime;

pub static RUNTIME: LazyLock<Runtime> = LazyLock::new(|| {
    tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .thread_name("toolbox-core")
        .enable_all()
        .build()
        .expect("failed to start tokio runtime")
});
