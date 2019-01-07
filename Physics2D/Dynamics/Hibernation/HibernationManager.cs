﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using tainicom.Aether.Physics2D.Collision;

namespace tainicom.Aether.Physics2D.Dynamics.Hibernation
{
    public class HibernationManager
    {
        private World ActiveWorld { get; set; }
        public World HibernatedWorld { get; private set; }
        public List<BaseActiveArea> ActiveAreas = new List<BaseActiveArea>();
        private List<Body> BodiesToHibernate = new List<Body>();
        private List<Body> BodiesToWake = new List<Body>();

        public HibernationManager(World world)
        {
            // store reference to active world
            this.ActiveWorld = world;

            // create a new world to store hibernated bodies
            this.HibernatedWorld = new World();
            // TODO: set properties to match active world? does it matter, since we're not stepping?


            // do an initial game-world pass to hibernate all bodies
            // going forward, all active bodies will be gauranteed in be in an AA, so this won't be neccessary.
            // OPTIMIZATION IDEA: allow constructor to accept a list of active areas and process them prior to 
            //                    this initial hibernate, to prevent hibernating bodies we're going to instantly unhibernate.
            foreach (var body in this.ActiveWorld.BodyList)
            {
                // for now, we're just going to mark everything as neededing hibernation.
                this.BodiesToHibernate.Add(body);
            }
        }

        public void Update()
        {
            // We only process this when the world is unlocked, so we may safely modify bodies, etc.
            if (this.ActiveWorld.IsLocked)
                throw new InvalidOperationException("The active World is locked.");
            // We only process this when the world is unlocked, so we may safely modify bodies, etc.
            if (this.HibernatedWorld.IsLocked)
                throw new InvalidOperationException("The hibernated World is locked.");

            #region update all ActiveArea AABBs 

            // remove all body tracking AA with no bodies...
            this.ActiveAreas.RemoveAll(aa => aa.AreaType == ActiveAreaType.BodyTracking && aa.Bodies.Count == 0 && (aa as BodyActiveArea).SecondsAgoCreated > 3.0f);

            // process all active areas
            foreach (var activeArea in this.ActiveAreas)
            {
                // update its bounding box
                activeArea.Update();
            }

            #endregion

            #region un-hibernate bodies ("wake")

            // process all active areas
            foreach ( var activeArea in this.ActiveAreas)
            {
                // get all hibernated bodies in its aabb
                var hibernatedBodiesInActiveArea = this.HibernatedWorld.FindBodiesInAABB(ref activeArea.AABB);

                // wake them
                foreach( var hibernatedBody in hibernatedBodiesInActiveArea)
                {
                    // clone into the active world
                    hibernatedBody.DeepClone(this.ActiveWorld);

                    // remove from the hibernated world
                    this.HibernatedWorld.Remove(hibernatedBody);
                }

                // NOTE: in this case, we don't actually store the bodies in the ActiveArea. anything which 
                //       collides is instantly woken, and there's no need to store a history.
            }

            #endregion

            #region Add new bodies and update position statuses for all bodies in active area.

            // process all active areas
            for (var i = 0; i < this.ActiveAreas.Count; i++)
            {
                // get current active area
                var activeArea = this.ActiveAreas[i];

                // Find all bodies which collide with active area AABB.
                var bodiesInActiveArea = this.ActiveWorld.FindBodiesInAABB(ref activeArea.AABB);

                // add all bodies which weren't already in AA
                var activeAreaBodies = activeArea.Bodies.Select(b => b.Body).ToList();
                for (var biaaIndex = bodiesInActiveArea.Count - 1; biaaIndex >= 0; biaaIndex--)
                {
                    // get body
                    var body = bodiesInActiveArea[biaaIndex];

                    // determine if this body is already in the AA
                    var isAlreadyInActiveArea = activeAreaBodies.Contains(body);

                    if (!isAlreadyInActiveArea)
                    {
                        // didn't find it. add it.
                        activeArea.Bodies.Add(new AreaBody(body));
                    }
                }

                // Loop over current ActiveArea bodies to update their statuses.
                for (var areaBodyIndex = activeArea.Bodies.Count - 1; areaBodyIndex >= 0; areaBodyIndex--)
                {
                    // get the body
                    var areaBody = activeArea.Bodies[areaBodyIndex];
                    var body = areaBody.Body;

                    // store the old status
                    areaBody.PriorStatus = areaBody.PositionStatus;

                    if (activeArea.AreaType == ActiveAreaType.BodyTracking && (activeArea as BodyActiveArea).IsExpired)
                    {
                        // because the active area is expired, we consider all bodies totlly outside of it, as it is about to disappear.
                        areaBody.PositionStatus = AreaBodyStatus.TotallyOut;
                    }
                    else
                    {
                        // determine whether this body is still touching the AA's AABB
                        var isTouchingActiveAreaAABB = bodiesInActiveArea.Contains(body);

                        if (!isTouchingActiveAreaAABB)
                        {
                            // it's not touching the AABB at all, so it's totally out.
                            areaBody.PositionStatus = AreaBodyStatus.TotallyOut;
                        }
                        else
                        {
                            // get body AABB
                            var bodyTransform = body.GetTransform();
                            AABB bodyAabb;
                            this.ActiveWorld.ContactManager.BroadPhase.GetFatAABB(body.ProxyId, out bodyAabb);

                            // at this point, we know it's touching. so it's just a matter of whether it's totally inside or partially inside.
                            // contains() returns 'true' only if the AABB is entirely within
                            // BUG HERE!!!
                            var isTotallyWithinActiveArea = activeArea.AABB.Contains(ref bodyAabb);

                            if (isTotallyWithinActiveArea)
                            {
                                // it's totally inside the ActiveArea AABB.
                                areaBody.PositionStatus = AreaBodyStatus.TotallyIn;
                            }
                            else
                            {
                                // at this point, we know it must be partially within.
                                areaBody.PositionStatus = AreaBodyStatus.PartiallyIn;
                            }
                        }
                    }
                }
            }

            #endregion

            #region Enact ramifications of status changes.

            // process all active areas
            for (var i = 0; i < this.ActiveAreas.Count; i++)
            {
                // get current active area
                var activeArea = this.ActiveAreas[i];

                // Loop over current ActiveArea bodies to update their statuses.
                for (var areaBodyIndex = activeArea.Bodies.Count - 1; areaBodyIndex >= 0; areaBodyIndex--)
                {
                    // get the body
                    var areaBody = activeArea.Bodies[areaBodyIndex];
                    var body = areaBody.Body;

                    if( areaBody.PositionStatus == AreaBodyStatus.Invalid )
                    {
                        throw new InvalidProgramException("All active area bodies should have their position status set by this point.");
                    }

                    var statusHasChanged = areaBody.PriorStatus != areaBody.PositionStatus;
                    if( statusHasChanged )
                    {
                        switch (areaBody.PositionStatus)
                        {
                            case AreaBodyStatus.TotallyIn:
                                // we have to ensure that this isn't its own body active area, so it doesn't remove itself...

                                // if there is a "BodyAA" for this body, then remove it, as that would be redundant.
                                // OPTIMIZATION IDEA: give all bodies a unique ID (will be needed for network anyhow) and store them in a hashlist for faster look-up.
                                // OPTIMIZATION IDEA2: give body a ref to its own tracking AA. if !null when set, throw. super-easy look-up.
                                var bodyAAs = this.ActiveAreas.Where(aa =>
                                    aa.AreaType == ActiveAreaType.BodyTracking
                                    && aa != activeArea
                                    && (aa as BodyActiveArea).TrackedBody == body
                                    && aa.Bodies.Select( aab => aab.Body ).Any( b => b == body ) );

                                switch (bodyAAs.Count())
                                {
                                    case 0:
                                        // no problem. move on.
                                        break;

                                    case 1:
                                        // destroy it! it is redundant.
                                        this.ActiveAreas.Remove(bodyAAs.First());
                                        break;

                                    default:
                                        // this really shouldn't happen. it means there is more than one bodyAA for this body.
                                        throw new InvalidProgramException("There is more than one ActiveArea for this body. This should never happen. There is a bug elsewhere.");
                                }

                                break;

                            case AreaBodyStatus.PartiallyIn:
                            case AreaBodyStatus.TotallyOut:

                                // determine if needs to create own AA. (non-zero linear or angular velocity)
                                var warrantsBodyActiveArea = body.AngularVelocity != 0 || body.LinearVelocity.Length() > 0;

                                if (warrantsBodyActiveArea) {

                                    // determine if has own AA
                                    var hasBodyActiveArea = this.ActiveAreas.Any(aa => aa.AreaType == ActiveAreaType.BodyTracking && (aa as BodyActiveArea).TrackedBody == body);

                                    if (!hasBodyActiveArea)
                                    {
                                        // create body AA
                                        this.ActiveAreas.Add(new BodyActiveArea(body)); 
                                    }
                                }
                               
                                break;
                        }

                        if (areaBody.PositionStatus == AreaBodyStatus.TotallyOut)
                        {
                            //if( activeArea.AreaType == ActiveAreaType.BodyTracking && areaBody.Body == (activeArea as BodyActiveArea).TrackedBody)
                            //{
                            //    // this AA is tracking that body, then remove the entire AA.
                            //    // NOTE: I doubt this ever happens... when would a body leave its own body-tracking area?
                            //    this.ActiveAreas.Remove(activeArea);
                            //}
                            //else
                            //{
                                // it's out of this AA, so we remove it from this AA.
                                activeArea.Bodies.Remove(areaBody);
                            //}

                            // if it's not in any other AA at this point, hibernate it.
                            var activeAreasContainingBody = this.ActiveAreas.Where(aa => 
                                aa.Bodies.Select(aab => aab.Body).Contains(body) 
                                //|| (aa.AreaType == ActiveAreaType.BodyTracking && (aa as BodyActiveArea).TrackedBody == body ) 
                                )
                                .ToList();
                                
                            if (activeAreasContainingBody.Count == 0)
                            {
                                this.BodiesToHibernate.Add(body);
                            }
                        }
                    }


                }
            }

            #endregion

            #region Hibernate all flagged bodies. 

            // hibernate all flagged bodies.
            foreach (var body in this.BodiesToHibernate)
            {
                // clone into the hibernated world
                body.DeepClone(this.HibernatedWorld);

                // remove from the active world
                this.ActiveWorld.Remove(body);
            }

            // clear the list
            this.BodiesToHibernate.Clear();

            #endregion

            #region Remove expired active areas

            // get expired active areas
            var expiredActiveAreas = this.ActiveAreas.Where(aa => aa.AreaType == ActiveAreaType.BodyTracking && (aa as BodyActiveArea).IsExpired).ToList();

            for (var i = expiredActiveAreas.Count - 1; i >= 0; i--)
            {
                var expiredActiveArea = expiredActiveAreas[i];
                if (expiredActiveArea.Bodies.Any())
                {
                    throw new InvalidProgramException("An expired active area still had bodies within it at time of deletion. This is an exception. They should be removed prior to this step.");
                }

                // just to be safe, we clear this ref too.
                //(expiredActiveArea as BodyActiveArea).TrackedBody = null;

                // remove expired active area
                this.ActiveAreas.Remove(expiredActiveArea);
            }

            #endregion
            //if match found in collide bodies, remove from collide bodies (basically ignore). update status to 'in' or 'partially in' depending on "contains" call. include ramification.

            // any bodies which are in the active area's list of bodies, but weren't fonud in the AABB query should be marked as 
            // "totally outside" and any which are new should be added to the collection.

            //var body = hibernatedBodyResult.Body;

            //// get body AABB
            //var bodyTransform = body.GetTransform();
            //AABB bodyAabb;
            //this.HibernatedWorld.ContactManager.BroadPhase.GetFatAABB(body.ProxyId, out bodyAabb);

            //// AABB is a box, but active areas are actually circles, so we do a radius check.
            //// NOTE: we use the active area's position and radius for the check rather than active area's aabb
            //var bodyDistance = Vector2.Distance(bodyAabb.Center, activeArea.Position);
            //var bodyInActiveAreaCircle = bodyDistance < (bodyAabb.Radius + activeArea.Radius); 

            //if( !bodyInActiveAreaCircle)
            //{
            //    // didn't quite make it. it's in the AABB, but not within the active area's circle. skip it.
            //    continue;
            //}

            // TODO: create AA for bodies only partilly within a AA
            // TODO: remove AA for bodies fully within this AA
            // TODO: hibernate bodies which are outside of AA and haven't collided...

        }
    }
}