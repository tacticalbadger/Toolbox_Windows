//! Desktop sizing rules shared by the remote-display panes.
//!
//! The RDP session itself runs in Microsoft's client control on the app
//! side; the rule for which desktop size to ask for lives here so it has a
//! test and one definition.

/// A desktop size in pixels.
#[derive(Debug, Clone, Copy, PartialEq, Eq, uniffi::Record)]
pub struct FrameSize {
    pub width: u16,
    pub height: u16,
}

/// Smallest and largest desktop edge Display Control accepts (MS-RDPEDISP).
const MIN_DESKTOP_EDGE: u32 = 200;
const MAX_DESKTOP_EDGE: u32 = 8192;

/// The desktop size to request for a wanted size in pixels. Each edge is
/// kept within what Display Control accepts, the height is made even, and
/// the width is rounded down to a multiple of 4: RDP bitmap rows are padded
/// to 4-pixel boundaries and some servers drop the connection when asked
/// for anything else (an odd width is refused outright by the protocol).
/// The app calls this before connecting and on every resize, so the size it
/// compares against is the size it actually asked for.
#[uniffi::export]
pub fn rdp_desktop_size(width: u32, height: u32) -> FrameSize {
    let width = width.clamp(MIN_DESKTOP_EDGE, MAX_DESKTOP_EDGE) & !3;
    let height = height.clamp(MIN_DESKTOP_EDGE, MAX_DESKTOP_EDGE) & !1;
    FrameSize {
        width: width as u16,
        height: height as u16,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn desktop_sizes_are_made_acceptable() {
        let size = |w, h| {
            let s = rdp_desktop_size(w, h);
            (s.width, s.height)
        };
        // Already fine.
        assert_eq!(size(1280, 800), (1280, 800));
        // A high-DPI pane: width down to a multiple of 4, height stays even.
        assert_eq!(size(2114, 1588), (2112, 1588));
        assert_eq!(size(2115, 1589), (2112, 1588));
        assert_eq!(size(1283, 801), (1280, 800));
        // Limits from MS-RDPEDISP.
        assert_eq!(size(0, 0), (200, 200));
        assert_eq!(size(50, 199), (200, 200));
        assert_eq!(size(20_000, 9_001), (8192, 8192));
        for w in 200..260 {
            assert_eq!(rdp_desktop_size(w, 300).width % 4, 0);
        }
    }
}
