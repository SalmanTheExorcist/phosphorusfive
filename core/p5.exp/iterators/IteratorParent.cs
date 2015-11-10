/*
 * Phosphorus Five, copyright 2014 - 2015, Thomas Hansen, phosphorusfive@gmail.com
 * Phosphorus Five is licensed under the terms of the MIT license, see the enclosed LICENSE file for details
 */

using System;
using System.Linq;
using System.Collections.Generic;
using p5.core;

namespace p5.exp.iterators
{
    /// <summary>
    ///     Returns all parent <see cref="phosphorus.core.Node" />s of previous iterator result.
    /// 
    ///     Example;
    ///     <pre>/.</pre>
    /// </summary>
    [Serializable]
    public class IteratorParent : Iterator
    {
        public override IEnumerable<Node> Evaluate (ApplicationContext context)
        {
            return from idxCurrent in Left.Evaluate (context) where idxCurrent.Parent != null select idxCurrent.Parent;
        }
    }
}