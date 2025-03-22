using Ownaudio.Processors;
using System;
using System.Collections.Generic;

namespace OwnaAvalonia.Processor
{
    public class FXProcessor : SampleProcessorBase
    {            
        private List<SampleProcessorBase>  _sampleProcessor = new List<SampleProcessorBase> ();

        public void AddFx(SampleProcessorBase _fx)
        {
           _sampleProcessor.Add(_fx);
        }
        public override void Process(Span<float> sample)
        { 
            foreach( var _fxProcessor in _sampleProcessor)
                _fxProcessor.Process(sample);
        }
    }
}
