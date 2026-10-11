import { useEffect, useState } from 'react';
import { api } from '../api/client';

// Each character's portrait with its backdrop removed, made once per page load.
const cache = new Map<string, Promise<string>>();

/**
 * The games' sprites are drawn on a flat backdrop colour that the game itself never shows. The
 * colour of the top-left pixel is taken for it and made transparent.
 */
function withoutBackdrop(url: string): Promise<string> {
  return new Promise((resolve) => {
    const image = new Image();
    image.onload = () => {
      const canvas = document.createElement('canvas');
      canvas.width = image.naturalWidth;
      canvas.height = image.naturalHeight;
      const context = canvas.getContext('2d');
      if (!context) return resolve(url);
      context.drawImage(image, 0, 0);
      const pixels = context.getImageData(0, 0, canvas.width, canvas.height);
      const data = pixels.data;
      const [r, g, b, a] = [data[0], data[1], data[2], data[3]];
      if (a !== 0) {
        for (let i = 0; i < data.length; i += 4) {
          if (data[i] === r && data[i + 1] === g && data[i + 2] === b) data[i + 3] = 0;
        }
        context.putImageData(pixels, 0, 0);
      }
      resolve(canvas.toDataURL());
    };
    image.onerror = () => resolve(url);
    image.src = url;
  });
}

/** A character's battle sprite. */
export function Portrait({ character, className }: { character: string; className?: string }) {
  const [source, setSource] = useState<string>();

  useEffect(() => {
    let current = true;
    let pending = cache.get(character);
    if (!pending) {
      pending = withoutBackdrop(api.portraitUrl(character));
      cache.set(character, pending);
    }
    pending.then((url) => current && setSource(url));
    return () => {
      current = false;
    };
  }, [character]);

  return source ? <img className={className} src={source} alt="" /> : <span className={className} />;
}