﻿namespace LivestreamRecorder.DB.Core
{
    public class UnitOfWork_Public : UnitOfWork
    {
        public UnitOfWork_Public(PublicContext publicContext)
            : base(publicContext)
        { }
    }
}
